﻿using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using ECommon.Components;
using ECommon.Logging;
using ECommon.Remoting;
using ECommon.Scheduling;
using ECommon.Serializing;
using ECommon.Socketing;
using ENode.Commanding;
using ENode.Configurations;
using ENode.EQueue;
using ENode.Eventing;
using ENode.Infrastructure;
using ENode.SqlServer;
using EQueue.Broker;
using EQueue.Clients.Consumers;
using EQueue.Configurations;
using EQueue.NameServer;
using EQueue.Protocols;
using EQueue.Protocols.NameServers;
using EQueue.Protocols.NameServers.Requests;
using EQueueMessage = EQueue.Protocols.Message;

namespace ENode.Tests
{
    public static class ENodeExtensions
    {
        private static NameServerController _nameServerController;
        private static BrokerController _broker;
        private static CommandService _commandService;
        private static CommandConsumer _commandConsumer;
        private static DomainEventPublisher _eventPublisher;
        private static DomainEventConsumer _eventConsumer;
        private static ApplicationMessagePublisher _applicationMessagePublisher;
        private static ApplicationMessageConsumer _applicationMessageConsumer;
        private static PublishableExceptionPublisher _publishableExceptionPublisher;
        private static PublishableExceptionConsumer _publishableExceptionConsumer;
        private static SocketRemotingClient _nameServerSocketRemotingClient;
        private static bool _isEQueueInitialized;
        private static bool _isEQueueStarted;

        public static ENodeConfiguration BuildContainer(this ENodeConfiguration enodeConfiguration)
        {
            enodeConfiguration.GetCommonConfiguration().BuildContainer();
            return enodeConfiguration;
        }
        public static ENodeConfiguration InitializeEQueue(this ENodeConfiguration enodeConfiguration)
        {
            if (_isEQueueInitialized)
            {
                return enodeConfiguration;
            }

            _commandService = new CommandService();
            _eventPublisher = new DomainEventPublisher();
            _applicationMessagePublisher = new ApplicationMessagePublisher();
            _publishableExceptionPublisher = new PublishableExceptionPublisher();

            _isEQueueInitialized = true;

            return enodeConfiguration;
        }
        public static ENodeConfiguration UseEQueue(this ENodeConfiguration enodeConfiguration,
            bool useMockDomainEventPublisher = false,
            bool useMockApplicationMessagePublisher = false,
            bool useMockPublishableExceptionPublisher = false)
        {
            var assemblies = new[] { Assembly.GetExecutingAssembly() };
            enodeConfiguration.RegisterTopicProviders(assemblies);

            var configuration = enodeConfiguration.GetCommonConfiguration();
            configuration.RegisterEQueueComponents();

            if (useMockDomainEventPublisher)
            {
                configuration.SetDefault<IMessagePublisher<DomainEventStreamMessage>, MockDomainEventPublisher>();
            }
            else
            {
                configuration.SetDefault<IMessagePublisher<DomainEventStreamMessage>, DomainEventPublisher>(_eventPublisher);
            }

            if (useMockApplicationMessagePublisher)
            {
                configuration.SetDefault<IMessagePublisher<IApplicationMessage>, MockApplicationMessagePublisher>();
            }
            else
            {
                configuration.SetDefault<IMessagePublisher<IApplicationMessage>, ApplicationMessagePublisher>(_applicationMessagePublisher);
            }

            if (useMockPublishableExceptionPublisher)
            {
                configuration.SetDefault<IMessagePublisher<IPublishableException>, MockPublishableExceptionPublisher>();
            }
            else
            {
                configuration.SetDefault<IMessagePublisher<IPublishableException>, PublishableExceptionPublisher>(_publishableExceptionPublisher);
            }

            configuration.SetDefault<ICommandService, CommandService>(_commandService);

            return enodeConfiguration;
        }
        public static ENodeConfiguration StartEQueue(this ENodeConfiguration enodeConfiguration)
        {
            if (_isEQueueStarted)
            {
                _commandService.InitializeENode();
                _eventPublisher.InitializeENode();
                _applicationMessagePublisher.InitializeENode();
                _publishableExceptionPublisher.InitializeENode();

                _commandConsumer.InitializeENode();
                _eventConsumer.InitializeENode();
                _applicationMessageConsumer.InitializeENode();
                _publishableExceptionConsumer.InitializeENode();

                return enodeConfiguration;
            }

            var brokerStorePath = @"d:\equeue-store-enode-ut";
            var brokerSetting = new BrokerSetting(chunkFileStoreRootPath: brokerStorePath);

            if (Directory.Exists(brokerStorePath))
            {
                Directory.Delete(brokerStorePath, true);
            }

            _nameServerController = new NameServerController();
            _broker = BrokerController.Create(brokerSetting);

            _commandService.InitializeEQueue(new CommandResultProcessor().Initialize(new IPEndPoint(SocketUtils.GetLocalIPV4(), 9001)));
            _eventPublisher.InitializeEQueue();
            _applicationMessagePublisher.InitializeEQueue();
            _publishableExceptionPublisher.InitializeEQueue();

            _commandConsumer = new CommandConsumer().InitializeEQueue(setting: new ConsumerSetting { ConsumeFromWhere = ConsumeFromWhere.LastOffset }).Subscribe("CommandTopic");
            _eventConsumer = new DomainEventConsumer().InitializeEQueue(setting: new ConsumerSetting { ConsumeFromWhere = ConsumeFromWhere.LastOffset }).Subscribe("EventTopic");
            _applicationMessageConsumer = new ApplicationMessageConsumer().InitializeEQueue(setting: new ConsumerSetting { ConsumeFromWhere = ConsumeFromWhere.LastOffset }).Subscribe("ApplicationMessageTopic");
            _publishableExceptionConsumer = new PublishableExceptionConsumer().InitializeEQueue(setting: new ConsumerSetting { ConsumeFromWhere = ConsumeFromWhere.LastOffset }).Subscribe("PublishableExceptionTopic");
            _nameServerSocketRemotingClient = new SocketRemotingClient("NameServerRemotingClient", new IPEndPoint(SocketUtils.GetLocalIPV4(), 9493));

            _nameServerController.Start();
            _broker.Start();
            _eventConsumer.Start();
            _commandConsumer.Start();
            _applicationMessageConsumer.Start();
            _publishableExceptionConsumer.Start();
            _applicationMessagePublisher.Start();
            _publishableExceptionPublisher.Start();
            _eventPublisher.Start();
            _commandService.Start();
            _nameServerSocketRemotingClient.Start();

            CreateTopic(Constants.CommandTopic);
            CreateTopic(Constants.EventTopic);
            CreateTopic(Constants.ApplicationMessageTopic);
            CreateTopic(Constants.ExceptionTopic);
            WaitAllProducerTopicQueuesAvailable();
            WaitAllConsumerLoadBalanceComplete();

            _isEQueueStarted = true;

            return enodeConfiguration;
        }

        public static ENodeConfiguration UseEventStore(this ENodeConfiguration enodeConfiguration, bool useMockEventStore = false)
        {
            var configuration = enodeConfiguration.GetCommonConfiguration();
            if (useMockEventStore)
            {
                configuration.SetDefault<IEventStore, MockEventStore>();
            }
            else
            {
                enodeConfiguration.UseSqlServerEventStore();
            }
            return enodeConfiguration;
        }
        public static ENodeConfiguration UsePublishedVersionStore(this ENodeConfiguration enodeConfiguration, bool useMockPublishedVersionStore = false)
        {
            var configuration = enodeConfiguration.GetCommonConfiguration();
            if (useMockPublishedVersionStore)
            {
                configuration.SetDefault<IPublishedVersionStore, MockPublishedVersionStore>();
            }
            else
            {
                enodeConfiguration.UseSqlServerPublishedVersionStore();
            }
            return enodeConfiguration;
        }

        private static void WaitAllProducerTopicQueuesAvailable()
        {
            var logger = ObjectContainer.Resolve<ILoggerFactory>().Create(typeof(ENodeExtensions).Name);
            var scheduleService = ObjectContainer.Resolve<IScheduleService>();
            var waitHandle = new ManualResetEvent(false);
            logger.Info("Waiting for all producer topic queues available, please wait for a moment...");
            scheduleService.StartTask("WaitAllProducerTopicQueuesAvailable", () =>
            {
                _commandService.Producer.ClientService.LoadTopicMessageQueuesFromNameServerAsync(Constants.CommandTopic).Wait();
                _eventPublisher.Producer.ClientService.LoadTopicMessageQueuesFromNameServerAsync(Constants.EventTopic).Wait();
                _applicationMessagePublisher.Producer.ClientService.LoadTopicMessageQueuesFromNameServerAsync(Constants.ApplicationMessageTopic).Wait();
                _publishableExceptionPublisher.Producer.ClientService.LoadTopicMessageQueuesFromNameServerAsync(Constants.ExceptionTopic).Wait();
                var availableQueues1 = _commandService.Producer.GetAvailableMessageQueues(Constants.CommandTopic);
                var availableQueues2 = _eventPublisher.Producer.GetAvailableMessageQueues(Constants.EventTopic);
                var availableQueues3 = _applicationMessagePublisher.Producer.GetAvailableMessageQueues(Constants.ApplicationMessageTopic);
                var availableQueues4 = _publishableExceptionPublisher.Producer.GetAvailableMessageQueues(Constants.ExceptionTopic);
                if (availableQueues1 != null
                 && availableQueues1 != null
                 && availableQueues1 != null
                 && availableQueues1 != null
                 && availableQueues1.Count == 4
                 && availableQueues2.Count == 4
                 && availableQueues3.Count == 4
                 && availableQueues4.Count == 4)
                {
                    waitHandle.Set();
                }
            }, 1000, 1000);

            waitHandle.WaitOne();
            scheduleService.StopTask("WaitAllProducerTopicQueuesAvailable");
            logger.Info("All producer topic queues are available.");
        }
        private static void WaitAllConsumerLoadBalanceComplete()
        {
            var logger = ObjectContainer.Resolve<ILoggerFactory>().Create(typeof(ENodeExtensions).Name);
            var scheduleService = ObjectContainer.Resolve<IScheduleService>();
            var waitHandle = new ManualResetEvent(false);
            logger.Info("Waiting for all consumer load balance complete, please wait for a moment...");
            scheduleService.StartTask("WaitAllConsumerLoadBalanceComplete", () =>
            {
                if (_eventConsumer.Consumer.GetCurrentQueues().Count() == 4
                 && _commandConsumer.Consumer.GetCurrentQueues().Count() == 4
                 && _applicationMessageConsumer.Consumer.GetCurrentQueues().Count() == 4
                 && _publishableExceptionConsumer.Consumer.GetCurrentQueues().Count() == 4)
                {
                    waitHandle.Set();
                }
            }, 1000, 1000);

            waitHandle.WaitOne();
            scheduleService.StopTask("WaitAllConsumerLoadBalanceComplete");
            logger.Info("All consumer load balance completed.");
        }
        private static void CreateTopic(string topic)
        {
            var binarySerializer = ObjectContainer.Resolve<IBinarySerializer>();
            var requestData = binarySerializer.Serialize(new CreateTopicForClusterRequest
            {
                ClusterName = "DefaultCluster",
                Topic = topic
            });
            var remotingRequest = new RemotingRequest((int)NameServerRequestCode.CreateTopic, requestData);
            var task = _nameServerSocketRemotingClient.InvokeAsync(remotingRequest, 30000);
            task.Wait();
            if (task.Result.ResponseCode != ResponseCode.Success)
            {
                throw new Exception(string.Format("CreateTopic failed, errorMessage: {0}", Encoding.UTF8.GetString(task.Result.ResponseBody)));
            }
        }
    }
}
