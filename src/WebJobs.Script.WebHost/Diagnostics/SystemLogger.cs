// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Host.Indexers;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Azure.WebJobs.Script.Configuration;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Diagnostics
{
    public class SystemLogger : ILogger, IDisposable
    {
        private const int DefaultFlushIntervalMs = 5000;
        private readonly IEventGenerator _eventGenerator;
        private readonly string _categoryName;
        private readonly string _functionName;
        private readonly bool _isUserFunction;
        private readonly string _hostInstanceId;
        private readonly IEnvironment _environment;
        private readonly LogLevel _logLevel;
        private readonly IDebugStateProvider _debugStateProvider;
        private readonly IScriptEventManager _eventManager;
        private readonly IExternalScopeProvider _scopeProvider;
        private readonly object _syncLock = new object();
        private readonly Timer _logFlushTimer;
        private readonly IOptionsMonitor<AppServiceOptions> _appServiceOptions;
        private List<LogItem> _logItemQueue;
        private bool _disposed = false;

        public SystemLogger(string hostInstanceId, string categoryName, IEventGenerator eventGenerator, IEnvironment environment,
            IDebugStateProvider debugStateProvider, IScriptEventManager eventManager, IExternalScopeProvider scopeProvider, IOptionsMonitor<AppServiceOptions> appServiceOptions, int flushIntervalMS = DefaultFlushIntervalMs)
        {
            _environment = environment;
            _eventGenerator = eventGenerator;
            _categoryName = categoryName ?? string.Empty;
            _logLevel = LogLevel.Debug;
            _functionName = LogCategories.IsFunctionCategory(_categoryName) ? _categoryName.Split('.')[1] : null;
            _isUserFunction = LogCategories.IsFunctionUserCategory(_categoryName);
            _hostInstanceId = hostInstanceId;
            _debugStateProvider = debugStateProvider;
            _eventManager = eventManager;
            _scopeProvider = scopeProvider;
            _logItemQueue = new List<LogItem>();
            _logFlushTimer = new Timer(TimerFlush, null, flushIntervalMS, flushIntervalMS);
            _appServiceOptions = appServiceOptions;
        }

        public IDisposable BeginScope<TState>(TState state) => _scopeProvider.Push(state);

        public bool IsEnabled(LogLevel logLevel)
        {
            if (_debugStateProvider.InDiagnosticMode)
            {
                // when in diagnostic mode, we log everything
                return true;
            }
            return logLevel >= _logLevel;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel) || IsUserLog(state))
            {
                return;
            }

            var logItem = new LogItem
            {
                LogLevel = logLevel,
                EventId = eventId,
                State = state,
                Exception = exception,
                EventTimestamp = DateTime.UtcNow
            };

            // scope values must be resolved now, before the item is queued and scope ends
            var scopeProps = _scopeProvider.GetScopeDictionary();
            var stateProps = state as IEnumerable<KeyValuePair<string, object>>;
            logItem.FunctionName = _functionName ?? Utility.ResolveFunctionName(stateProps, scopeProps);
            logItem.InvocationId = Utility.GetValueFromScope(scopeProps, ScriptConstants.LogPropertyFunctionInvocationIdKey);

            if (formatter != null)
            {
                logItem.Formatter = (s, e) =>
                {
                    return formatter.Invoke(state, exception);
                };
            }

            lock (_syncLock)
            {
                _logItemQueue.Add(logItem);
            }

            if (logLevel == LogLevel.Error ||
                logLevel == LogLevel.Critical)
            {
                // when errors are logged we want to force an immediate flush
                // to ensure they don't get lost if the process dies
                Flush();
            }
        }

        private void LogCore(LogItem logItem)
        {
            // propagate special exceptions through the EventManager
            var stateProps = logItem.State as IEnumerable<KeyValuePair<string, object>>;
            string source = _categoryName ?? Utility.GetStateValueOrDefault<string>(stateProps, ScriptConstants.LogPropertySourceKey);
            if (logItem.Exception is FunctionIndexingException && _eventManager != null)
            {
                _eventManager.Publish(new FunctionIndexingEvent(nameof(FunctionIndexingException), source, logItem.Exception));
            }

            // If we don't have a message, there's nothing to log.
            string formattedMessage = logItem.Formatter?.Invoke(logItem.State, logItem.Exception);
            if (string.IsNullOrEmpty(formattedMessage))
            {
                return;
            }

            // Apply standard event properties
            // Note: we must be sure to default any null values to empty string
            // otherwise the ETW event will fail to be persisted (silently)
            string summary = formattedMessage ?? string.Empty;
            string eventName = !string.IsNullOrEmpty(logItem.EventId.Name) ? logItem.EventId.Name : Utility.GetStateValueOrDefault<string>(stateProps, ScriptConstants.LogPropertyEventNameKey) ?? string.Empty;
            string activityId = Utility.GetStateValueOrDefault<string>(stateProps, ScriptConstants.LogPropertyActivityIdKey) ?? string.Empty;
            string functionName = logItem.FunctionName ?? string.Empty;
            string functionInvocationId = logItem.InvocationId ?? string.Empty;

            string innerExceptionType = string.Empty;
            string innerExceptionMessage = string.Empty;
            string details = string.Empty;
            if (logItem.Exception != null)
            {
                // Populate details from the exception.
                if (string.IsNullOrEmpty(functionName) && logItem.Exception is FunctionInvocationException fex)
                {
                    functionName = string.IsNullOrEmpty(fex.MethodName) ? string.Empty : fex.MethodName.Replace("Host.Functions.", string.Empty);
                }

                (innerExceptionType, innerExceptionMessage, details) = logItem.Exception.GetExceptionDetails();
                innerExceptionMessage = innerExceptionMessage ?? string.Empty;
            }

            _eventGenerator.LogFunctionTraceEvent(logItem.LogLevel, _appServiceOptions.CurrentValue.SubscriptionId, _appServiceOptions.CurrentValue.AppName, functionName, eventName, source, details, summary, innerExceptionType, innerExceptionMessage, functionInvocationId, _hostInstanceId, activityId, _appServiceOptions.CurrentValue.RuntimeSiteName, _appServiceOptions.CurrentValue.SlotName, logItem.EventTimestamp);
        }

        private bool IsUserLog<TState>(TState state)
        {
            // User logs are determined by either the category or the presence of the LogPropertyIsUserLogKey
            // in the log state.
            // This check is extra defensive; the 'Function.{FunctionName}.User' category should never occur here
            // as the SystemLoggerProvider checks that before creating a Logger.

            return _isUserFunction ||
                (state is IEnumerable<KeyValuePair<string, object>> stateDict &&
                Utility.GetStateBoolValue(stateDict, ScriptConstants.LogPropertyIsUserLogKey) == true);
        }

        internal void Flush()
        {
            if (_logItemQueue.Count > 0)
            {
                List<LogItem> currentLogItems = null;
                lock (_syncLock)
                {
                    if (_logItemQueue.Count > 0)
                    {
                        currentLogItems = _logItemQueue;
                        _logItemQueue = new List<LogItem>();
                    }
                }

                foreach (var logItem in currentLogItems)
                {
                    LogCore(logItem);
                }
            }
        }

        private void TimerFlush(object state)
        {
            Flush();
        }

        public virtual void Dispose()
        {
            if (!_disposed)
            {
                Flush();
                _logFlushTimer.Dispose();

                _disposed = true;
            }
        }

        private class LogItem
        {
            public DateTime EventTimestamp { get; set; }

            public LogLevel LogLevel { get; set; }

            public EventId EventId { get; set; }

            public string FunctionName { get; set; }

            public string InvocationId { get; set; }

            public object State { get; set; }

            public Exception Exception { get; set; }

            public Func<object, Exception, string> Formatter { get; set; }
        }
    }
}