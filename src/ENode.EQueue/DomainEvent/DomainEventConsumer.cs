﻿using System.Text;
using ECommon.Components;
using ECommon.Logging;
using ECommon.Serializing;
using ENode.Commanding;
using ENode.Eventing;
using ENode.Infrastructure;
using EQueue.Clients.Consumers;
using EQueue.Protocols;
using IQueueMessageHandler = EQueue.Clients.Consumers.IMessageHandler;

namespace ENode.EQueue
{
    public class DomainEventConsumer : IQueueMessageHandler
    {
        private const string DefaultEventConsumerGroup = "EventConsumerGroup";
        private SendReplyService _sendReplyService;
        private IJsonSerializer _jsonSerializer;
        private IEventSerializer _eventSerializer;
        private IMessageProcessor<ProcessingDomainEventStreamMessage, DomainEventStreamMessage> _messageProcessor;
        private ILogger _logger;
        private bool _sendEventHandledMessage;

        public Consumer Consumer { get; private set; }

        public DomainEventConsumer InitializeENode(bool sendEventHandledMessage = true)
        {
            _sendReplyService = new SendReplyService("EventConsumerSendReplyService");
            _jsonSerializer = ObjectContainer.Resolve<IJsonSerializer>();
            _eventSerializer = ObjectContainer.Resolve<IEventSerializer>();
            _messageProcessor = ObjectContainer.Resolve<IMessageProcessor<ProcessingDomainEventStreamMessage, DomainEventStreamMessage>>();
            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(GetType().FullName);
            _sendEventHandledMessage = sendEventHandledMessage;
            return this;
        }
        public DomainEventConsumer InitializeEQueue(string groupName = null, ConsumerSetting setting = null, bool sendEventHandledMessage = true)
        {
            InitializeENode(sendEventHandledMessage);
            Consumer = new Consumer(groupName ?? DefaultEventConsumerGroup, setting ?? new ConsumerSetting
            {
                MessageHandleMode = MessageHandleMode.Sequential,
                ConsumeFromWhere = ConsumeFromWhere.FirstOffset
            }, "DomainEventConsumer");
            return this;
        }

        public DomainEventConsumer Start()
        {
            _sendReplyService.Start();
            Consumer.SetMessageHandler(this).Start();
            return this;
        }
        public DomainEventConsumer Subscribe(string topic)
        {
            Consumer.Subscribe(topic);
            return this;
        }
        public DomainEventConsumer Shutdown()
        {
            Consumer.Stop();
            if (_sendEventHandledMessage)
            {
                _sendReplyService.Stop();
            }
            return this;
        }

        void IQueueMessageHandler.Handle(QueueMessage queueMessage, IMessageContext context)
        {
            var message = _jsonSerializer.Deserialize<EventStreamMessage>(Encoding.UTF8.GetString(queueMessage.Body));
            var domainEventStreamMessage = ConvertToDomainEventStream(message);
            var processContext = new DomainEventStreamProcessContext(this, domainEventStreamMessage, queueMessage, context);
            var processingMessage = new ProcessingDomainEventStreamMessage(domainEventStreamMessage, processContext);
            _logger.DebugFormat("ENode event message received, messageId: {0}, aggregateRootId: {1}, aggregateRootType: {2}, version: {3}", domainEventStreamMessage.Id, domainEventStreamMessage.AggregateRootStringId, domainEventStreamMessage.AggregateRootTypeName, domainEventStreamMessage.Version);
            _messageProcessor.Process(processingMessage);
        }

        private DomainEventStreamMessage ConvertToDomainEventStream(EventStreamMessage message)
        {
            var domainEventStreamMessage = new DomainEventStreamMessage(
                message.CommandId,
                message.AggregateRootId,
                message.Version,
                message.AggregateRootTypeName,
                _eventSerializer.Deserialize<IDomainEvent>(message.Events),
                message.Items)
            {
                Id = message.Id,
                Timestamp = message.Timestamp
            };
            return domainEventStreamMessage;
        }

        class DomainEventStreamProcessContext : EQueueProcessContext
        {
            private readonly DomainEventConsumer _eventConsumer;
            private readonly DomainEventStreamMessage _domainEventStreamMessage;

            public DomainEventStreamProcessContext(DomainEventConsumer eventConsumer, DomainEventStreamMessage domainEventStreamMessage, QueueMessage queueMessage, IMessageContext messageContext)
                : base(queueMessage, messageContext)
            {
                _eventConsumer = eventConsumer;
                _domainEventStreamMessage = domainEventStreamMessage;
            }

            public override void NotifyMessageProcessed()
            {
                base.NotifyMessageProcessed();

                if (!_eventConsumer._sendEventHandledMessage)
                {
                    return;
                }

                if (!_domainEventStreamMessage.Items.TryGetValue("CommandReplyAddress", out string replyAddress) || string.IsNullOrEmpty(replyAddress))
                {
                    return;
                }
                _domainEventStreamMessage.Items.TryGetValue("CommandResult", out string commandResult);

                _eventConsumer._sendReplyService.SendReply((int)CommandReturnType.EventHandled, new DomainEventHandledMessage
                {
                    CommandId = _domainEventStreamMessage.CommandId,
                    AggregateRootId = _domainEventStreamMessage.AggregateRootId,
                    CommandResult = commandResult
                }, replyAddress);
            }
        }
    }
}
