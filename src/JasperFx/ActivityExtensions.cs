using System.ComponentModel;
using System.Diagnostics;

namespace JasperFx;

// Required due to the .NET 9 version of the DiagnosticSource package not being binary compatible with .NET 8. Remove in vNext.
#if NET8_0
public static class ActivityExtensions
{
        /// <summary>
        /// .NET 8 compatible version of .NET 9's AddException. This is an internal JasperFx API, don't take a dependency on this.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static Activity AddException(this Activity activity, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            var exceptionTags = new ActivityTagsCollection();
            
            const string ExceptionEventName = "exception";
            const string ExceptionMessageTag = "exception.message";
            const string ExceptionStackTraceTag = "exception.stacktrace";
            const string ExceptionTypeTag = "exception.type";
            
            exceptionTags.Add(new KeyValuePair<string, object?>(ExceptionMessageTag, exception.Message));
            exceptionTags.Add(new KeyValuePair<string, object?>(ExceptionStackTraceTag, exception.ToString()));
            exceptionTags.Add(new KeyValuePair<string, object?>(ExceptionTypeTag, exception.GetType().ToString()));
        

            return activity.AddEvent(new ActivityEvent(ExceptionEventName,default, exceptionTags));
        }
} 
#endif
