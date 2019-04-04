using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Datadog.Trace.Headers;
using Datadog.Trace.Logging;

namespace Datadog.Trace.ClrProfiler.Integrations
{
    /// <summary>
    /// The ASP.NET Core MVC 2 integration.
    /// </summary>
    public sealed class AspNetCoreMvc2Integration : IDisposable
    {
        internal const string OperationName = "aspnet-coremvc.request";
        private const string HttpContextKey = "__Datadog.Trace.ClrProfiler.Integrations." + nameof(AspNetCoreMvc2Integration);

        private static readonly ILog Log = LogProvider.GetLogger(typeof(AspNetCoreMvc2Integration));

        private static Action<object, object, object, object> _beforeAction;
        private static Action<object, object, object, object> _afterAction;

        private readonly dynamic _httpContext;
        private readonly Scope _scope;

        /// <summary>
        /// Initializes a new instance of the <see cref="AspNetCoreMvc2Integration"/> class.
        /// </summary>
        /// <param name="actionDescriptorObj">An ActionDescriptor with information about the current action.</param>
        /// <param name="httpContextObj">The HttpContext for the current request.</param>
        public AspNetCoreMvc2Integration(object actionDescriptorObj, object httpContextObj)
        {
            try
            {
                dynamic actionDescriptor = actionDescriptorObj;
                var controllerName = (actionDescriptor.ControllerName as string)?.ToLowerInvariant();
                var actionName = (actionDescriptor.ActionName as string)?.ToLowerInvariant();

                _httpContext = httpContextObj;
                string httpMethod = _httpContext.Request.Method.ToUpperInvariant();
                string url = GetDisplayUrl(_httpContext.Request).ToLowerInvariant();

                SpanContext propagatedContext = null;

                if (Tracer.Instance.ActiveScope == null)
                {
                    try
                    {
                        // extract propagated http headers
                        IEnumerable requestHeaders = _httpContext.Request.Headers;
                        int headerCount = _httpContext.Request.Headers.Count;
                        var headersCollection = new DictionaryHeadersCollection(headerCount);

                        foreach (dynamic header in requestHeaders)
                        {
                            string key = header.Key;
                            string[] values = header.Value.ToArray();
                            headersCollection.Add(key, values);
                        }

                        propagatedContext = SpanContextPropagator.Instance.Extract(headersCollection);
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorException("Error extracting propagated HTTP headers.", ex);
                    }
                }

                _scope = Tracer.Instance.StartActive(OperationName, propagatedContext);
                var span = _scope.Span;
                span.Type = SpanTypes.Web;
                span.ResourceName = $"{httpMethod} {controllerName}.{actionName}";
                span.SetTag(Tags.HttpMethod, httpMethod);
                span.SetTag(Tags.HttpUrl, url);
                span.SetTag(Tags.AspNetController, controllerName);
                span.SetTag(Tags.AspNetAction, actionName);
            }
            catch (Exception) when (DisposeObject(_scope))
            {
                // unreachable code
                throw;
            }
        }

        /// <summary>
        /// Wrapper method used to instrument Microsoft.AspNetCore.Mvc.Internal.MvcCoreDiagnosticSourceExtensions.BeforeAction()
        /// </summary>
        /// <param name="diagnosticSource">The DiagnosticSource that this extension method was called on.</param>
        /// <param name="actionDescriptor">An ActionDescriptor with information about the current action.</param>
        /// <param name="httpContext">The HttpContext for the current request.</param>
        /// <param name="routeData">A RouteData with information about the current route.</param>
        [InterceptMethod(
            CallerAssembly = "Microsoft.AspNetCore.Mvc.Core",
            TargetAssembly = "Microsoft.AspNetCore.Mvc.Core",
            TargetType = "Microsoft.AspNetCore.Mvc.Internal.MvcCoreDiagnosticSourceExtensions")]
        public static void BeforeAction(
            object diagnosticSource,
            object actionDescriptor,
            object httpContext,
            object routeData)
        {
            AspNetCoreMvc2Integration integration = null;

            try
            {
                integration = new AspNetCoreMvc2Integration(actionDescriptor, httpContext);

                if (httpContext.TryGetPropertyValue("Items", out IDictionary<object, object> contextItems))
                {
                    contextItems[HttpContextKey] = integration;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorExceptionForFilter($"Error creating {nameof(AspNetCoreMvc2Integration)}.", ex);
            }

            try
            {
                if (_beforeAction == null)
                {
                    var assembly = actionDescriptor.GetType().GetTypeInfo().Assembly;
                    var type = assembly.GetType("Microsoft.AspNetCore.Mvc.Internal.MvcCoreDiagnosticSourceExtensions");

                    _beforeAction = DynamicMethodBuilder<Action<object, object, object, object>>.CreateMethodCallDelegate(
                        type,
                        "BeforeAction");
                }
            }
            catch (Exception ex)
            {
                // profiled app will continue working without DiagnosticSource
                Log.ErrorException("Error calling Microsoft.AspNetCore.Mvc.Internal.MvcCoreDiagnosticSourceExtensions.BeforeAction()", ex);
            }

            try
            {
                // call the original method, catching and rethrowing any unhandled exceptions
                _beforeAction?.Invoke(diagnosticSource, actionDescriptor, httpContext, routeData);
            }
            catch (Exception ex) when (integration?.SetException(ex) ?? false)
            {
                // unreachable code
                throw;
            }
        }

        /// <summary>
        /// Wrapper method used to instrument Microsoft.AspNetCore.Mvc.Internal.MvcCoreDiagnosticSourceExtensions.AfterAction()
        /// </summary>
        /// <param name="diagnosticSource">The DiagnosticSource that this extension method was called on.</param>
        /// <param name="actionDescriptor">An ActionDescriptor with information about the current action.</param>
        /// <param name="httpContext">The HttpContext for the current request.</param>
        /// <param name="routeData">A RouteData with information about the current route.</param>
        [InterceptMethod(
            CallerAssembly = "Microsoft.AspNetCore.Mvc.Core",
            TargetAssembly = "Microsoft.AspNetCore.Mvc.Core",
            TargetType = "Microsoft.AspNetCore.Mvc.Internal.MvcCoreDiagnosticSourceExtensions")]
        public static void AfterAction(
            object diagnosticSource,
            object actionDescriptor,
            object httpContext,
            object routeData)
        {
            AspNetCoreMvc2Integration integration = null;

            try
            {
                if (httpContext.TryGetPropertyValue("Items", out IDictionary<object, object> contextItems))
                {
                    integration = contextItems?[HttpContextKey] as AspNetCoreMvc2Integration;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorExceptionForFilter($"Error accessing {nameof(AspNetCoreMvc2Integration)}.", ex);
            }

            try
            {
                if (_afterAction == null)
                {
                    var type = actionDescriptor.GetType().Assembly.GetType("Microsoft.AspNetCore.Mvc.Internal.MvcCoreDiagnosticSourceExtensions");

                    _afterAction = DynamicMethodBuilder<Action<object, object, object, object>>.CreateMethodCallDelegate(
                        type,
                        "AfterAction");
                }
            }
            catch
            {
                // TODO: log this as an instrumentation error, we cannot call instrumented method,
                // profiled app will continue working without DiagnosticSource
            }

            try
            {
                // call the original method, catching and rethrowing any unhandled exceptions
                _afterAction?.Invoke(diagnosticSource, actionDescriptor, httpContext, routeData);
            }
            catch (Exception ex)
            {
                integration?.SetException(ex);

                throw;
            }
            finally
            {
                integration?.Dispose();
            }
        }

        /// <summary>
        /// Tags the current span as an error. Called when an unhandled exception is thrown in the instrumented method.
        /// </summary>
        /// <param name="ex">The exception that was thrown and not handled in the instrumented method.</param>
        /// <returns>Always <c>false</c>.</returns>
        public bool SetException(Exception ex)
        {
            _scope?.Span?.SetException(ex);
            return false;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            try
            {
                if (_httpContext != null)
                {
                    _scope?.Span?.SetTag("http.status_code", _httpContext.Response.StatusCode.ToString());
                }
            }
            finally
            {
                _scope?.Dispose();
            }
        }

        private static string GetDisplayUrl(dynamic request)
        {
            string host = request.Host.Value;
            string pathBase = request.PathBase.Value;
            string path = request.Path.Value;
            string queryString = request.QueryString.Value;

            return new StringBuilder(request.Scheme.Length + "://".Length + host.Length + pathBase.Length + path.Length + queryString.Length).Append(request.Scheme)
                                                                                                                                             .Append("://")
                                                                                                                                             .Append(host)
                                                                                                                                             .Append(pathBase)
                                                                                                                                             .Append(path)
                                                                                                                                             .Append(queryString)
                                                                                                                                             .ToString();
        }

        private bool DisposeObject(IDisposable disposable)
        {
            disposable?.Dispose();
            return false;
        }
    }
}
