// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Host.Loggers;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.WebHost.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Diagnostics
{
    // Adapter for capturing SDK events and logging them to tables.
    internal class FunctionInstanceLogger : IAsyncCollector<FunctionInstanceLogEntry>
    {
        private const string Key = "metadata";

        private readonly ILogWriter _writer;
        private readonly IMetricsLogger _metrics;
        private readonly IFunctionMetadataManager _metadataManager;
        private ConcurrentDictionary<string, FunctionMetadata> _functionMetadataMap = new ConcurrentDictionary<string, FunctionMetadata>(StringComparer.OrdinalIgnoreCase);

        public FunctionInstanceLogger(
            IFunctionMetadataManager metadataManager,
            IMetricsLogger metrics,
            IHostIdProvider hostIdProvider,
            IConfiguration configuration,
            ILoggerFactory loggerFactory,
            IDelegatingHandlerProvider delegatingHandlerProvider)
            : this(metadataManager, metrics)
        {
            if (hostIdProvider == null)
            {
                throw new ArgumentNullException(nameof(hostIdProvider));
            }

            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            if (delegatingHandlerProvider == null)
            {
                throw new ArgumentNullException(nameof(delegatingHandlerProvider));
            }

            string accountConnectionString = configuration.GetWebJobsConnectionString(ConnectionStringNames.Dashboard);
            if (accountConnectionString != null)
            {
                CloudStorageAccount account = CloudStorageAccount.Parse(accountConnectionString);
                var restConfig = new RestExecutorConfiguration { DelegatingHandler = delegatingHandlerProvider.Create() };
                var tableClientConfig = new TableClientConfiguration { RestExecutorConfiguration = restConfig };

                var client = new CloudTableClient(account.TableStorageUri, account.Credentials, tableClientConfig);
                var tableProvider = LogFactory.NewLogTableProvider(client);

                ILogger logger = loggerFactory.CreateLogger(ScriptConstants.LogCategoryHostGeneral);

                string hostId = hostIdProvider.GetHostIdAsync(CancellationToken.None).GetAwaiter().GetResult() ?? "default";
                string containerName = Environment.MachineName;
                _writer = LogFactory.NewWriter(hostId, containerName, tableProvider, (e) => OnException(e, logger));
            }
        }

        internal FunctionInstanceLogger(IFunctionMetadataManager metadataManager, IMetricsLogger metrics)
        {
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _metadataManager = metadataManager ?? throw new ArgumentNullException(nameof(metadataManager));
        }

        public async Task AddAsync(FunctionInstanceLogEntry item, CancellationToken cancellationToken = default(CancellationToken))
        {
            FunctionInstanceMonitor monitor;
            item.Properties.TryGetValue(Key, out monitor);

            if (item.EndTime.HasValue)
            {
                // Function Completed
                bool success = item.ErrorDetails == null;
                monitor.End(success);
            }
            else
            {
                // Function Started
                if (monitor == null)
                {
                    var function = GetFunctionMetadata(item.LogName);
                    if (function != null)
                    {
                        monitor = new FunctionInstanceMonitor(function, _metrics, item.FunctionInstanceId);
                        item.Properties[Key] = monitor;
                        monitor.Start();
                    }
                    else
                    {
                        // This exception will cause the function to not get executed.
                        throw new InvalidOperationException($"Missing function.json for '{item.LogName}'.");
                    }
                }
            }

            if (_writer != null)
            {
                await _writer.AddAsync(new FunctionInstanceLogItem
                {
                    FunctionInstanceId = item.FunctionInstanceId,
                    FunctionName = item.LogName,
                    StartTime = item.StartTime,
                    EndTime = item.EndTime,
                    TriggerReason = item.TriggerReason,
                    Arguments = item.Arguments,
                    ErrorDetails = item.ErrorDetails,
                    LogOutput = item.LogOutput,
                    ParentId = item.ParentId
                });
            }
        }

        private FunctionMetadata GetFunctionMetadata(string functionName)
        {
            return _functionMetadataMap.GetOrAdd(functionName, s =>
            {
                var functions = _metadataManager.GetFunctionMetadata();
                return functions.FirstOrDefault(p => Utility.FunctionNamesMatch(p.Name, s));
            });
        }

        public Task FlushAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_writer == null)
            {
                return Task.CompletedTask;
            }

            return _writer.FlushAsync();
        }

        public static void OnException(Exception exception, ILogger logger)
        {
            logger.LogError($"Error writing logs to table storage: {exception.ToString()}", exception);
        }
    }
}
