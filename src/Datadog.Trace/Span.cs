using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Datadog.Trace.ExtensionMethods;
using Datadog.Trace.Interfaces;
using Datadog.Trace.Logging;

namespace Datadog.Trace
{
    /// <summary>
    /// A Span represents a logical unit of work in the system. It may be
    /// related to other spans by parent/children relationships. The span
    /// tracks the duration of an operation as well as associated metadata in
    /// the form of a resource name, a service name, and user defined tags.
    /// </summary>
    public class Span : IDisposable, ISpan
    {
        private static readonly ILog Log = LogProvider.For<Span>();

        private readonly object _lock = new object();

        internal Span(SpanContext context, DateTimeOffset? start)
        {
            Context = context;
            ServiceName = context.ServiceName;
            StartTime = start ?? Context.TraceContext.UtcNow;
        }

        /// <summary>
        /// Gets or sets operation name
        /// </summary>
        public string OperationName { get; set; }

        /// <summary>
        /// Gets or sets the resource name
        /// </summary>
        public string ResourceName { get; set; }

        /// <summary>
        /// Gets or sets the type of request this span represents (ex: web, db).
        /// Not to be confused with span kind.
        /// </summary>
        /// <seealso cref="SpanTypes"/>
        public string Type { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this span represents an error
        /// </summary>
        public bool Error { get; set; }

        /// <summary>
        /// Gets or sets the service name.
        /// </summary>
        public string ServiceName
        {
            get => Context.ServiceName;
            set => Context.ServiceName = value;
        }

        /// <summary>
        /// Gets the trace's unique identifier.
        /// </summary>
        public ulong TraceId => Context.TraceId;

        /// <summary>
        /// Gets the span's unique identifier.
        /// </summary>
        public ulong SpanId => Context.SpanId;

        internal SpanContext Context { get; }

        internal DateTimeOffset StartTime { get; }

        internal TimeSpan Duration { get; private set; }

        internal ConcurrentDictionary<string, string> Tags { get; } = new ConcurrentDictionary<string, string>();

        internal ConcurrentDictionary<string, double> Metrics { get; } = new ConcurrentDictionary<string, double>();

        internal bool IsFinished { get; private set; }

        internal bool IsRootSpan => Context?.TraceContext?.RootSpan == this;

        /// <summary>
        /// Returns a <see cref="string" /> that represents this instance.
        /// </summary>
        /// <returns>
        /// A <see cref="string" /> that represents this instance.
        /// </returns>
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"TraceId: {Context.TraceId}");
            sb.AppendLine($"ParentId: {Context.ParentId}");
            sb.AppendLine($"SpanId: {Context.SpanId}");
            sb.AppendLine($"ServiceName: {ServiceName}");
            sb.AppendLine($"OperationName: {OperationName}");
            sb.AppendLine($"Resource: {ResourceName}");
            sb.AppendLine($"Type: {Type}");
            sb.AppendLine($"Start: {StartTime}");
            sb.AppendLine($"Duration: {Duration}");
            sb.AppendLine($"Error: {Error}");
            sb.AppendLine("Meta:");

            if (Tags != null)
            {
                foreach (var kv in Tags)
                {
                    sb.Append($"\t{kv.Key}:{kv.Value}");
                }
            }

            sb.AppendLine("Metrics:");

            if (Metrics != null && Metrics.Count > 0)
            {
                foreach (var kv in Metrics)
                {
                    sb.Append($"\t{kv.Key}:{kv.Value}");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Add a the specified tag to this span.
        /// </summary>
        /// <param name="key">The tag's key.</param>
        /// <param name="value">The tag's value.</param>
        /// <returns>This span to allow method chaining.</returns>
        public Span SetTag(string key, string value)
        {
            if (IsFinished)
            {
                Log.Debug("SetTag should not be called after the span was closed");
                return this;
            }

            if (value == null)
            {
                // Agent doesn't accept null tag values,
                // remove them instead
                Tags.TryRemove(key, out _);
                return this;
            }

            // some tags have special meaning
            switch (key)
            {
                case Trace.Tags.SamplingPriority:
                    if (Enum.TryParse(value, out SamplingPriority samplingPriority) &&
                        Enum.IsDefined(typeof(SamplingPriority), samplingPriority))
                    {
                        // allow setting the sampling priority via a tag
                        Context.TraceContext.SamplingPriority = samplingPriority;
                    }

                    break;
                case Trace.Tags.ForceKeep:
                    if (value.ToBoolean() ?? false)
                    {
                        // user-friendly tag to set UserKeep priority
                        Context.TraceContext.SamplingPriority = SamplingPriority.UserKeep;
                    }

                    break;
                case Trace.Tags.Analytics:
                    var boolean = value.ToBoolean();

                    if (boolean == true)
                    {
                        SetMetric(Trace.Tags.Analytics, 1.0);
                    }
                    else if (boolean == false)
                    {
                        SetMetric(Trace.Tags.Analytics, null);
                    }
                    else if (double.TryParse(
                        value,
                        NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
                        CultureInfo.InvariantCulture,
                        out double analyticsSampleRate))
                    {
                        SetMetric(Trace.Tags.Analytics, analyticsSampleRate);
                    }

                    break;
                default:
                    // if not a special tag, just add it to the tag bag
                    Tags[key] = value;
                    break;
            }

            return this;
        }

        /// <summary>
        /// Add a the specified tag to this span.
        /// </summary>
        /// <param name="key">The tag's key.</param>
        /// <param name="value">The tag's value.</param>
        /// <returns>This span to allow method chaining.</returns>
        ISpan ISpan.SetTag(string key, string value)
            => SetTag(key, value);

        /// <summary>
        /// Record the end time of the span and flushes it to the backend.
        /// After the span has been finished all modifications will be ignored.
        /// </summary>
        public void Finish()
        {
            Finish(Context.TraceContext.UtcNow);
        }

        /// <summary>
        /// Explicitly set the end time of the span and flushes it to the backend.
        /// After the span has been finished all modifications will be ignored.
        /// </summary>
        /// <param name="finishTimestamp">Explicit value for the end time of the Span</param>
        public void Finish(DateTimeOffset finishTimestamp)
        {
            var shouldCloseSpan = false;
            lock (_lock)
            {
                ResourceName = ResourceName ?? OperationName;
                if (!IsFinished)
                {
                    Duration = finishTimestamp - StartTime;
                    if (Duration < TimeSpan.Zero)
                    {
                        Duration = TimeSpan.Zero;
                    }

                    IsFinished = true;
                    shouldCloseSpan = true;
                }
            }

            if (shouldCloseSpan)
            {
                Context.TraceContext.CloseSpan(this);
            }
        }

        /// <summary>
        /// Record the end time of the span and flushes it to the backend.
        /// After the span has been finished all modifications will be ignored.
        /// </summary>
        public void Dispose()
        {
            Finish();
        }

        /// <summary>
        /// Add the StackTrace and other exception metadata to the span
        /// </summary>
        /// <param name="exception">The exception.</param>
        public void SetException(Exception exception)
        {
            Error = true;

            if (exception != null)
            {
                // for AggregateException, use the first inner exception until we can support multiple errors.
                // there will be only one error in most cases, and even if there are more and we lose
                // the other ones, it's still better than the generic "one or more errors occurred" message.
                if (exception is AggregateException aggregateException && aggregateException.InnerExceptions.Count > 0)
                {
                    exception = aggregateException.InnerExceptions[0];
                }

                SetTag(Trace.Tags.ErrorMsg, exception.Message);
                SetTag(Trace.Tags.ErrorStack, exception.StackTrace);
                SetTag(Trace.Tags.ErrorType, exception.GetType().ToString());
            }
        }

        /// <summary>
        /// Proxy to SetException without return value
        /// See <see cref="Span.SetException(Exception)"/> for more information
        /// </summary>
        /// <param name="exception">The exception.</param>
        void ISpan.SetException(Exception exception)
            => SetException(exception);

        /// <summary>
        /// Gets the value (or default/null if the key is not a valid tag) of a tag with the key value passed
        /// </summary>
        /// <param name="key">The tag's key</param>
        /// <returns> The value for the tag with the key specified, or null if the tag does not exist</returns>
        public string GetTag(string key)
            => Tags.TryGetValue(key, out var value)
                   ? value
                   : null;

        internal bool SetExceptionForFilter(Exception exception)
        {
            SetException(exception);
            return false;
        }

        internal double? GetMetric(string key)
        {
            return Metrics.TryGetValue(key, out double value)
                       ? value
                       : default;
        }

        internal Span SetMetric(string key, double? value)
        {
            if (value == null)
            {
                Metrics.TryRemove(key, out _);
            }
            else
            {
                Metrics[key] = value.Value;
            }

            return this;
        }
    }
}
