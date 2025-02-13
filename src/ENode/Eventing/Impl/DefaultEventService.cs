﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ECommon.IO;
using ECommon.Logging;
using ECommon.Scheduling;
using ECommon.Serializing;
using ENode.Commanding;
using ENode.Configurations;
using ENode.Domain;
using ENode.Infrastructure;

namespace ENode.Eventing.Impl
{
    public class DefaultEventService : IEventService
    {
        #region Private Variables

        private readonly int _eventMailboxCount;
        private readonly List<EventMailBox> _eventMailboxList;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IScheduleService _scheduleService;
        private readonly ITypeNameProvider _typeNameProvider;
        private readonly IMemoryCache _memoryCache;
        private readonly IAggregateRootFactory _aggregateRootFactory;
        private readonly IAggregateStorage _aggregateStorage;
        private readonly IEventStore _eventStore;
        private readonly IMessagePublisher<DomainEventStreamMessage> _domainEventPublisher;
        private readonly IOHelper _ioHelper;
        private readonly ILogger _logger;
        private readonly int _batchSize;
        private readonly int _timeoutSeconds;
        private readonly string _taskName;

        #endregion

        #region Constructors

        public DefaultEventService(
            IJsonSerializer jsonSerializer,
            IScheduleService scheduleService,
            ITypeNameProvider typeNameProvider,
            IMemoryCache memoryCache,
            IAggregateRootFactory aggregateRootFactory,
            IAggregateStorage aggregateStorage,
            IEventStore eventStore,
            IMessagePublisher<DomainEventStreamMessage> domainEventPublisher,
            IOHelper ioHelper,
            ILoggerFactory loggerFactory)
        {
            _eventMailboxList = new List<EventMailBox>();
            _ioHelper = ioHelper;
            _jsonSerializer = jsonSerializer;
            _scheduleService = scheduleService;
            _typeNameProvider = typeNameProvider;
            _memoryCache = memoryCache;
            _aggregateRootFactory = aggregateRootFactory;
            _aggregateStorage = aggregateStorage;
            _eventStore = eventStore;
            _domainEventPublisher = domainEventPublisher;
            _logger = loggerFactory.Create(GetType().FullName);
            _eventMailboxCount = ENodeConfiguration.Instance.Setting.EventMailBoxCount;
            _batchSize = ENodeConfiguration.Instance.Setting.EventMailBoxPersistenceMaxBatchSize;
            _timeoutSeconds = ENodeConfiguration.Instance.Setting.AggregateRootMaxInactiveSeconds;
            _taskName = "CleanInactiveAggregates_" + DateTime.Now.Ticks + new Random().Next(10000);

            for (var i = 0; i < _eventMailboxCount; i++)
            {
                _eventMailboxList.Add(new EventMailBox(i.ToString(), _batchSize, BatchPersistEventCommittingContexts, _logger));
            }
        }

        #endregion

        #region Public Methods

        public void CommitDomainEventAsync(EventCommittingContext eventCommittingContext)
        {
            var eventMailboxIndex = GetEventMailBoxIndex(eventCommittingContext.EventStream.AggregateRootId);
            var eventMailbox = _eventMailboxList[eventMailboxIndex];
            eventMailbox.EnqueueMessage(eventCommittingContext);
        }
        public void PublishDomainEventAsync(ProcessingCommand processingCommand, DomainEventStream eventStream)
        {
            if (eventStream.Items == null || eventStream.Items.Count == 0)
            {
                eventStream.Items = processingCommand.Items;
            }
            var eventStreamMessage = new DomainEventStreamMessage(processingCommand.Message.Id, eventStream.AggregateRootId, eventStream.Version, eventStream.AggregateRootTypeName, eventStream.Events, eventStream.Items);
            PublishDomainEventAsync(processingCommand, eventStreamMessage, 0);
        }
        public void Start()
        {
            _scheduleService.StartTask(_taskName, CleanInactiveMailbox, ENodeConfiguration.Instance.Setting.ScanExpiredAggregateIntervalMilliseconds, ENodeConfiguration.Instance.Setting.ScanExpiredAggregateIntervalMilliseconds);
        }
        public void Stop()
        {
            _scheduleService.StopTask(_taskName);
        }

        #endregion

        #region Private Methods

        private int GetEventMailBoxIndex(string aggregateRootId)
        {
            unchecked
            {
                int hash = 23;
                foreach (char c in aggregateRootId)
                {
                    hash = (hash << 5) - hash + c;
                }
                if (hash < 0)
                {
                    hash = Math.Abs(hash);
                }
                return hash % _eventMailboxCount;
            }
        }
        private void BatchPersistEventCommittingContexts(IList<EventCommittingContext> committingContexts)
        {
            if (committingContexts == null || committingContexts.Count == 0)
            {
                return;
            }

            if (_eventStore.SupportBatchAppendEvent)
            {
                BatchPersistEventAsync(committingContexts, 0);
            }
            else
            {
                PersistEventOneByOne(committingContexts);
            }
        }
        private void BatchPersistEventAsync(IList<EventCommittingContext> committingContexts, int retryTimes)
        {
            _ioHelper.TryAsyncActionRecursively("BatchPersistEventAsync",
            () => _eventStore.BatchAppendAsync(committingContexts.Select(x => x.EventStream)),
            currentRetryTimes => BatchPersistEventAsync(committingContexts, currentRetryTimes),
            result =>
            {
                var appendResult = result.Data;
                if (appendResult == EventAppendResult.Success)
                {
                    var eventMailBox = committingContexts.First().MailBox;
                    _logger.DebugFormat("Batch persist event success, routingKey: {0}, eventStreamCount: {1}, minEventVersion: {2}, maxEventVersion: {3}", eventMailBox.RoutingKey, committingContexts.Count, committingContexts.First().EventStream.Version, committingContexts.Last().EventStream.Version);

                    Task.Factory.StartNew(x =>
                    {
                        var contextList = x as IList<EventCommittingContext>;
                        foreach (var context in contextList)
                        {
                            PublishDomainEventAsync(context.ProcessingCommand, context.EventStream);
                        }
                    }, committingContexts);

                    foreach (var committingContext in committingContexts)
                    {
                        committingContext.MailBox.CompleteMessage(committingContext, true);
                    }

                    eventMailBox.CompleteRun();
                }
                else if (appendResult == EventAppendResult.DuplicateEvent)
                {
                    var eventMailBox = committingContexts.First().MailBox;
                    _logger.WarnFormat("Batch persist event has concurrent version conflict, routingKey: {0}, eventStreamCount: {1}, minEventVersion: {2}, maxEventVersion: {3}", eventMailBox.RoutingKey, committingContexts.Count, committingContexts.First().EventStream.Version, committingContexts.Last().EventStream.Version);
                    ProcessDuplicateEvent(committingContexts.First());
                }
                else if (appendResult == EventAppendResult.DuplicateCommand)
                {
                    PersistEventOneByOne(committingContexts);
                }
            },
            () => string.Format("[contextListCount:{0}]", committingContexts.Count),
            errorMessage =>
            {
                _logger.Fatal(string.Format("Batch persist event has unknown exception, the code should not be run to here, errorMessage: {0}", errorMessage));
            },
            retryTimes, true);
        }
        private void PersistEventOneByOne(IList<EventCommittingContext> contextList)
        {
            ConcatContexts(contextList);
            PersistEventAsync(contextList[0], 0);
        }
        private void PersistEventAsync(EventCommittingContext context, int retryTimes)
        {
            _ioHelper.TryAsyncActionRecursively("PersistEventAsync",
            () => _eventStore.AppendAsync(context.EventStream),
            currentRetryTimes => PersistEventAsync(context, currentRetryTimes),
            result =>
            {
                if (result.Data == EventAppendResult.Success)
                {
                    _logger.DebugFormat("Persist event success, eventStream: {0}", context.EventStream);

                    Task.Factory.StartNew(x =>
                    {
                        var currentContext = x as EventCommittingContext;
                        PublishDomainEventAsync(currentContext.ProcessingCommand, currentContext.EventStream);
                    }, context);

                    if (context.Next != null)
                    {
                        PersistEventAsync(context.Next, 0);
                    }
                    else
                    {
                        context.MailBox.CompleteMessage(context, true);
                        context.MailBox.CompleteRun();
                    }
                }
                else if (result.Data == EventAppendResult.DuplicateEvent)
                {
                    _logger.WarnFormat("Persist event has concurrent version conflict, eventStream: {0}", context.EventStream);
                    ProcessDuplicateEvent(context);
                }
                else if (result.Data == EventAppendResult.DuplicateCommand)
                {
                    _logger.WarnFormat("Persist event has duplicate command, eventStream: {0}", context.EventStream);
                    ResetCommandMailBoxConsumingSequence(context, context.ProcessingCommand.Sequence + 1).ContinueWith(t => { }).ConfigureAwait(false);
                    TryToRepublishEventAsync(context, 0);
                }
            },
            () => string.Format("[eventStream:{0}]", context.EventStream),
            errorMessage =>
            {
                _logger.Fatal(string.Format("Persist event has unknown exception, the code should not be run to here, errorMessage: {0}", errorMessage));
            },
            retryTimes, true);
        }
        private void ProcessDuplicateEvent(EventCommittingContext eventCommittingContext)
        {
            if (eventCommittingContext.EventStream.Version == 1)
            {
                HandleFirstEventDuplicationAsync(eventCommittingContext, 0);
            }
            else
            {
                ResetCommandMailBoxConsumingSequence(eventCommittingContext, eventCommittingContext.ProcessingCommand.Sequence).ContinueWith(t => { }).ConfigureAwait(false);
            }
        }
        private async Task ResetCommandMailBoxConsumingSequence(EventCommittingContext context, long consumingSequence)
        {
            var processingCommand = context.ProcessingCommand;
            var command = processingCommand.Message;
            var commandMailBox = processingCommand.MailBox;
            var eventMailBox = context.MailBox as EventMailBox;
            var aggregateRootId = context.EventStream.AggregateRootId;

            commandMailBox.Pause();

            try
            {
                eventMailBox.RemoveAggregateAllEventCommittingContexts(aggregateRootId);
                var refreshedAggregateRoot = await _memoryCache.RefreshAggregateFromEventStoreAsync(context.EventStream.AggregateRootTypeName, aggregateRootId);
                commandMailBox.ResetConsumingSequence(consumingSequence);
            }
            catch (Exception ex)
            {
                _logger.Error(string.Format("ResetCommandMailBoxConsumingOffset has unknown exception, commandId: {0}, aggregateRootId: {1}", command.Id, command.AggregateRootId), ex);
            }
            finally
            {
                commandMailBox.Resume();
                commandMailBox.TryRun();
                eventMailBox.CompleteRun();
            }
        }
        private void TryToRepublishEventAsync(EventCommittingContext context, int retryTimes)
        {
            var command = context.ProcessingCommand.Message;

            _ioHelper.TryAsyncActionRecursively("FindEventByCommandIdAsync",
            () => _eventStore.FindAsync(command.AggregateRootId, command.Id),
            currentRetryTimes => TryToRepublishEventAsync(context, currentRetryTimes),
            result =>
            {
                var existingEventStream = result.Data;
                if (existingEventStream != null)
                {
                    //这里，我们需要再重新做一遍发布事件这个操作；
                    //之所以要这样做是因为虽然该command产生的事件已经持久化成功，但并不表示事件已经发布出去了；
                    //因为有可能事件持久化成功了，但那时正好机器断电了，则发布事件都没有做；
                    PublishDomainEventAsync(context.ProcessingCommand, existingEventStream);
                }
                else
                {
                    //到这里，说明当前command想添加到eventStore中时，提示command重复，但是尝试从eventStore中取出该command时却找不到该command。
                    //出现这种情况，我们就无法再做后续处理了，这种错误理论上不会出现，除非eventStore的Add接口和Get接口出现读写不一致的情况；
                    //框架会记录错误日志，让开发者排查具体是什么问题。
                    var errorMessage = string.Format("Command should be exist in the event store, but we cannot find it from the event store, this should not be happen, and we cannot continue again. commandType:{0}, commandId:{1}, aggregateRootId:{2}",
                        command.GetType().Name,
                        command.Id,
                        command.AggregateRootId);
                    _logger.Fatal(errorMessage);
                    var commandResult = new CommandResult(CommandStatus.Failed, command.Id, command.AggregateRootId, "Command should be exist in the event store, but we cannot find it from the event store.", typeof(string).FullName);
                    CompleteCommand(context.ProcessingCommand, commandResult);
                }
            },
            () => string.Format("[aggregateRootId:{0}, commandId:{1}]", command.AggregateRootId, command.Id),
            errorMessage =>
            {
                _logger.Fatal(string.Format("Find event by commandId has unknown exception, the code should not be run to here, errorMessage: {0}", errorMessage));
            },
            retryTimes, true);
        }
        private void HandleFirstEventDuplicationAsync(EventCommittingContext context, int retryTimes)
        {
            _ioHelper.TryAsyncActionRecursively("FindFirstEventByVersion",
            () => _eventStore.FindAsync(context.EventStream.AggregateRootId, 1),
            currentRetryTimes => HandleFirstEventDuplicationAsync(context, currentRetryTimes),
            result =>
            {
                var firstEventStream = result.Data;
                if (firstEventStream != null)
                {
                    //判断是否是同一个command，如果是，则再重新做一遍发布事件；
                    //之所以要这样做，是因为虽然该command产生的事件已经持久化成功，但并不表示事件也已经发布出去了；
                    //有可能事件持久化成功了，但那时正好机器断电了，则发布事件都没有做；
                    if (context.ProcessingCommand.Message.Id == firstEventStream.CommandId)
                    {
                        ResetCommandMailBoxConsumingSequence(context, context.ProcessingCommand.Sequence + 1).ContinueWith(t =>
                        {
                            PublishDomainEventAsync(context.ProcessingCommand, firstEventStream);
                        }).ConfigureAwait(false);
                    }
                    else
                    {
                        //如果不是同一个command，则认为是两个不同的command重复创建ID相同的聚合根，我们需要记录错误日志，然后通知当前command的处理完成；
                        var errorMessage = string.Format("Duplicate aggregate creation. current commandId:{0}, existing commandId:{1}, aggregateRootId:{2}, aggregateRootTypeName:{3}",
                            context.ProcessingCommand.Message.Id,
                            firstEventStream.CommandId,
                            firstEventStream.AggregateRootId,
                            firstEventStream.AggregateRootTypeName);
                        _logger.Error(errorMessage);
                        ResetCommandMailBoxConsumingSequence(context, context.ProcessingCommand.Sequence + 1).ContinueWith(t =>
                        {
                            var commandResult = new CommandResult(CommandStatus.Failed, context.ProcessingCommand.Message.Id, context.EventStream.AggregateRootId, "Duplicate aggregate creation.", typeof(string).FullName);
                            CompleteCommand(context.ProcessingCommand, commandResult);
                        }).ConfigureAwait(false);
                    }
                }
                else
                {
                    var errorMessage = string.Format("Duplicate aggregate creation, but we cannot find the existing eventstream from eventstore, this should not be happen, and we cannot continue again. commandId:{0}, aggregateRootId:{1}, aggregateRootTypeName:{2}",
                        context.EventStream.CommandId,
                        context.EventStream.AggregateRootId,
                        context.EventStream.AggregateRootTypeName);
                    _logger.Fatal(errorMessage);
                    ResetCommandMailBoxConsumingSequence(context, context.ProcessingCommand.Sequence + 1).ContinueWith(t =>
                    {
                        var commandResult = new CommandResult(CommandStatus.Failed, context.ProcessingCommand.Message.Id, context.EventStream.AggregateRootId, "Duplicate aggregate creation, but we cannot find the existing eventstream from eventstore.", typeof(string).FullName);
                        CompleteCommand(context.ProcessingCommand, commandResult);
                    }).ConfigureAwait(false);
                }
            },
            () => string.Format("[eventStream:{0}]", context.EventStream),
            errorMessage =>
            {
                _logger.Fatal(string.Format("Find the first version of event has unknown exception, the code should not be run to here, errorMessage: {0}", errorMessage));
            },
            retryTimes, true);
        }
        private void PublishDomainEventAsync(ProcessingCommand processingCommand, DomainEventStreamMessage eventStream, int retryTimes)
        {
            _ioHelper.TryAsyncActionRecursively("PublishEventAsync",
            () => _domainEventPublisher.PublishAsync(eventStream),
            currentRetryTimes => PublishDomainEventAsync(processingCommand, eventStream, currentRetryTimes),
            result =>
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.DebugFormat("Publish event success, {0}", eventStream);
                }
                var commandHandleResult = processingCommand.CommandExecuteContext.GetResult();
                var commandResult = new CommandResult(CommandStatus.Success, processingCommand.Message.Id, eventStream.AggregateRootId, commandHandleResult, typeof(string).FullName);
                CompleteCommand(processingCommand, commandResult);
            },
            () => string.Format("[eventStream:{0}]", eventStream),
            errorMessage =>
            {
                _logger.Fatal(string.Format("Publish event has unknown exception, the code should not be run to here, errorMessage: {0}", errorMessage));
            },
            retryTimes, true);
        }
        private void ConcatContexts(IList<EventCommittingContext> contextList)
        {
            for (var i = 0; i < contextList.Count - 1; i++)
            {
                var currentContext = contextList[i];
                var nextContext = contextList[i + 1];
                currentContext.Next = nextContext;
            }
        }
        private Task CompleteCommand(ProcessingCommand processingCommand, CommandResult commandResult)
        {
            return processingCommand.MailBox.CompleteMessage(processingCommand, commandResult);
        }
        private void CleanInactiveMailbox()
        {
            //TODO
            //var inactiveList = new List<KeyValuePair<string, EventMailBox>>();
            //foreach (var pair in _mailboxDict)
            //{
            //    if (pair.Value.IsInactive(_timeoutSeconds) && !pair.Value.IsRunning)
            //    {
            //        inactiveList.Add(pair);
            //    }
            //}
            //foreach (var pair in inactiveList)
            //{
            //    EventMailBox removed;
            //    if (_mailboxDict.TryRemove(pair.Key, out removed))
            //    {
            //        _logger.InfoFormat("Removed inactive event mailbox, aggregateRootId: {0}", pair.Key);
            //    }
            //}
        }

        #endregion
    }
}
