using CK.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CK.Monitoring.Hosting
{
    sealed class GrandOutputConfigurationInitializer
    {
        /// <summary>
        /// Simply dispose the given <see cref="_grandOutput"/> when the server close.
        /// </summary>
        class HostedService : IHostedService
        {
            readonly GrandOutput _grandOutput;

            public HostedService( GrandOutput grandOutput )
            {
                _grandOutput = grandOutput;
            }

            public Task StartAsync( CancellationToken cancellationToken ) => Task.CompletedTask;

            public Task StopAsync( CancellationToken cancellationToken )
            {
                _grandOutput.Dispose();
                return Task.CompletedTask;
            }
        }

        readonly GrandOutput _target;
        readonly GrandOutputLoggerAdapterProvider _loggerProvider;
        IConfigurationSection _section;
        IDisposable _changeToken;
        readonly bool _isDefaultGrandOutput;
        bool _trackUnhandledException;

        public GrandOutputConfigurationInitializer( GrandOutput target )
        {
            if( target == null )
            {
                _isDefaultGrandOutput = true;
                target = GrandOutput.EnsureActiveDefault( new GrandOutputConfiguration() );
            }
            _target = target;
            _loggerProvider = new GrandOutputLoggerAdapterProvider( _target );
        }

        public void Initialize( IHostBuilder builder, IHostEnvironment env, ILoggingBuilder aspNetLogs, IConfigurationSection section )
        {
            _section = section;
            if( _isDefaultGrandOutput && LogFile.RootLogPath == null )
            {
                LogFile.RootLogPath = Path.GetFullPath( Path.Combine( env.ContentRootPath, _section["LogPath"] ?? "Logs" ) );
            }
            aspNetLogs.AddProvider( _loggerProvider );
            var reloadToken = _section.GetReloadToken();
            _changeToken = reloadToken.RegisterChangeCallback( OnConfigurationChanged, this );
            // We do not handle CancellationTokenRegistration.Dispose here.
            // The target is disposing: everything will be discarded, included
            // this instance of initializer.
            _target.DisposingToken.Register( () =>
            {
                _changeToken.Dispose();
                ConfigureGlobalListeners( false, false, false );
            } );
            // This is required so that default configuration with Text handler
            // is applied if there is no section.
            ApplyDynamicConfiguration( true );

            builder.ConfigureServices( ( ctx, services ) =>
            {
                 services.AddHostedService( ( p ) => new HostedService( _target ) );
            } );
        }

        public void PostInitialze( IHostApplicationLifetime lifetime )
        {
            lifetime.ApplicationStopped.Register( () => _target.Dispose() );
        }

        void ConfigureGlobalListeners( bool trackUnhandledException, bool net461DiagnosticTrace, bool aspNetLogs )
        {
            _loggerProvider._running = aspNetLogs;
            if( trackUnhandledException != _trackUnhandledException )
            {
                if( trackUnhandledException )
                {
                    AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                    TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
                }
                else
                {
                    AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                    TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
                }
                _trackUnhandledException = trackUnhandledException;
            }
        }

        void ApplyDynamicConfiguration( bool initialConfigMustWaitForApplication )
        {
            bool trackUnhandledException = !String.Equals( _section["LogUnhandledExceptions"], "false", StringComparison.OrdinalIgnoreCase );
            bool net461DiagnosticTrace = !String.Equals( _section["HandleDiagnosticsEvents"], "false", StringComparison.OrdinalIgnoreCase );
            bool aspNetLogs = !String.Equals( _section["HandleAspNetLogs"], "false", StringComparison.OrdinalIgnoreCase );
            ConfigureGlobalListeners( trackUnhandledException, net461DiagnosticTrace, aspNetLogs );
            GrandOutputConfiguration c;
            var gSection = _section.GetSection( "GrandOutput" );
            if( gSection.Exists() )
            {
                var ctorPotentialParams = new[] { typeof( IConfigurationSection ) };
                c = new GrandOutputConfiguration();
                gSection.Bind( c );
                var hSection = gSection.GetSection( "Handlers" );
                foreach( var hConfig in hSection.GetChildren() )
                {
                    // Checks for single value and not section.
                    // This is required for handlers that have no configuration properties:
                    // "Handlers": { "Console": true } does the job.
                    // The only case of "falsiness" we consider here is "false":
                    // we ignore the key is this case.
                    string value = hConfig.Value;
                    if( !String.IsNullOrWhiteSpace( value )
                        && String.Equals( value, "false", StringComparison.OrdinalIgnoreCase ) ) continue;

                    // Resolve configuration type using one of two available strings:
                    // 1. From "ConfigurationType" property, inside the value object
                    Type resolved = null;
                    string configTypeProperty = hConfig.GetValue( "ConfigurationType", string.Empty );
                    if( string.IsNullOrEmpty( configTypeProperty ) )
                    {
                        // No ConfigurationType property:
                        // Resolve using the key, outside the value object
                        resolved = TryResolveType( hConfig.Key );
                    }
                    else
                    {
                        // With ConfigurationType property:
                        // Try and resolve property and key, in that order
                        resolved = TryResolveType( configTypeProperty );
                        if( resolved == null )
                        {
                            resolved = TryResolveType( hConfig.Key );
                        }
                    }
                    if( resolved == null )
                    {
                        if( string.IsNullOrEmpty( configTypeProperty ) )
                        {
                            ActivityMonitor.CriticalErrorCollector.Add( new CKException( $"Unable to resolve type '{hConfig.Key}'." ), nameof( GrandOutputConfigurationInitializer ) );
                        }
                        else
                        {
                            ActivityMonitor.CriticalErrorCollector.Add( new CKException( $"Unable to resolve type '{configTypeProperty}' (from Handlers.{hConfig.Key}.ConfigurationType) or '{hConfig.Key}'." ), nameof( GrandOutputConfigurationInitializer ) );
                        }
                        continue;
                    }
                    try
                    {
                        var ctorWithConfig = resolved.GetConstructor( ctorPotentialParams );
                        object config;
                        if( ctorWithConfig != null ) config = ctorWithConfig.Invoke( new[] { hConfig } );
                        else
                        {
                            config = Activator.CreateInstance( resolved );
                            hConfig.Bind( config );
                        }
                        c.AddHandler( (IHandlerConfiguration)config );
                    }
                    catch( Exception ex )
                    {
                        ActivityMonitor.CriticalErrorCollector.Add( ex, nameof( GrandOutputConfigurationInitializer ) );
                    }
                }
            }
            else
            {
                c = new GrandOutputConfiguration()
                    .AddHandler( new CK.Monitoring.Handlers.TextFileConfiguration() { Path = "Text" } );
            }
            _target.ApplyConfiguration( c, initialConfigMustWaitForApplication );
        }

        void OnUnobservedTaskException( object sender, UnobservedTaskExceptionEventArgs e )
        {
            ActivityMonitor.CriticalErrorCollector.Add( e.Exception, "UnobservedTaskException" );
            e.SetObserved();
        }

        void OnUnhandledException( object sender, UnhandledExceptionEventArgs e )
        {
            var ex = e.ExceptionObject as Exception;
            if( ex != null ) ActivityMonitor.CriticalErrorCollector.Add( ex, "UnhandledException" );
            else
            {
                string errText = e.ExceptionObject.ToString();
                _target.ExternalLog( Core.LogLevel.Fatal, errText, GrandOutput.CriticalErrorTag );
            }
        }

        Type TryResolveType( string name )
        {
            Type resolved;
            if( name.IndexOf( ',' ) >= 0 )
            {
                // It must be an assembly qualified name.
                // Weaken its name and try to load it.
                // If it fails and the name does not end with "Configuration" tries it.
                string fullTypeName, assemblyFullName, assemblyName, versionCultureAndPublicKeyToken;
                if( SimpleTypeFinder.SplitAssemblyQualifiedName( name, out fullTypeName, out assemblyFullName )
                    && SimpleTypeFinder.SplitAssemblyFullName( assemblyFullName, out assemblyName, out versionCultureAndPublicKeyToken ) )
                {
                    var weakTypeName = fullTypeName + ", " + assemblyName;
                    resolved = SimpleTypeFinder.RawGetType( weakTypeName, false );
                    if( resolved != null ) return IsHandlerConfiguration( resolved );
                    if( !fullTypeName.EndsWith( "Configuration" ) )
                    {
                        weakTypeName = fullTypeName + "Configuration, " + assemblyName;
                        resolved = SimpleTypeFinder.RawGetType( weakTypeName, false );
                        if( resolved != null ) return IsHandlerConfiguration( resolved );
                    }
                }
                return null;
            }
            // This is a simple type name: try to find the type name in already loaded assemblies.
            var configTypes = AppDomain.CurrentDomain.GetAssemblies()
                                .SelectMany( a => a.GetTypes() )
                                .Where( t => typeof( IHandlerConfiguration ).IsAssignableFrom( t ) )
                                .ToList();
            var nameWithC = !name.EndsWith( "Configuration" ) ? name + "Configuration" : null;
            if( name.IndexOf( '.' ) > 0 )
            {
                // It is a FullName.
                resolved = configTypes.FirstOrDefault( t => t.FullName == name
                                                            || (nameWithC != null && t.FullName == nameWithC) );
            }
            else
            {
                // There is no dot in the name.
                resolved = configTypes.FirstOrDefault( t => t.Name == name
                                                            || (nameWithC != null && t.Name == nameWithC) );
            }
            return resolved;
        }

        static Type IsHandlerConfiguration( Type candidate )
        {
            if( typeof( IHandlerConfiguration ).IsAssignableFrom( candidate ) ) return candidate;
            return null;
        }

        static void OnConfigurationChanged( object obj )
        {
            Debug.Assert( obj is GrandOutputConfigurationInitializer );
            var initializer = (GrandOutputConfigurationInitializer)obj;
            initializer.ApplyDynamicConfiguration( false );
            initializer.RenewChangeToken();
        }

        void RenewChangeToken()
        {
            // Disposes the previous change token.
            _changeToken.Dispose();
            // Reacquires the token: using this as the state keeps this object alive.
            var reloadToken = _section.GetReloadToken();
            _changeToken = reloadToken.RegisterChangeCallback( OnConfigurationChanged, this );
        }
    }
}
