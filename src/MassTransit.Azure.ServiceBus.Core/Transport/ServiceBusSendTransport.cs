﻿// Copyright 2007-2018 Chris Patterson, Dru Sellers, Travis Smith, et. al.
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace MassTransit.Azure.ServiceBus.Core.Transport
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Contexts;
    using GreenPipes;
    using GreenPipes.Agents;
    using Logging;
    using MassTransit.Pipeline.Observables;
    using MassTransit.Scheduling;
    using Microsoft.Azure.ServiceBus;
    using Transports;


    /// <summary>
    /// Send messages to an azure transport using the message sender.
    /// 
    /// May be sensible to create a IBatchSendTransport that allows multiple
    /// messages to be sent as a single batch (perhaps using Tx support?)
    /// </summary>
    public class ServiceBusSendTransport :
        Supervisor,
        ISendTransport
    {
        static readonly ILog _log = Logger.Get<ServiceBusSendTransport>();
        readonly Uri _address;
        readonly SendObservable _observers;

        readonly IPipeContextSource<SendEndpointContext> _source;

        public ServiceBusSendTransport(IPipeContextSource<SendEndpointContext> source, Uri address)
        {
            _source = source;
            _address = address;
            _observers = new SendObservable();
        }

        Task ISendTransport.Send<T>(T message, IPipe<SendContext<T>> pipe, CancellationToken cancellationToken)
        {
            IPipe<SendEndpointContext> clientPipe = Pipe.ExecuteAsync<SendEndpointContext>(async clientContext =>
            {
                var context = new AzureServiceBusSendContext<T>(message, cancellationToken);

                try
                {
                    await pipe.Send(context).ConfigureAwait(false);

                    if (message is CancelScheduledMessage cancelScheduledMessage
                        && (context.TryGetScheduledMessageId(out var sequenceNumber)
                            || context.TryGetSequencyNumber(cancelScheduledMessage.TokenId, out sequenceNumber)))
                    {
                        try
                        {
                            await clientContext.CancelScheduledSend(sequenceNumber).ConfigureAwait(false);

                            if (_log.IsDebugEnabled)
                                _log.DebugFormat("Canceled Scheduled: {0} {1}", sequenceNumber, clientContext.EntityPath);
                        }
                        catch (MessageNotFoundException exception)
                        {
                            if (_log.IsDebugEnabled)
                                _log.DebugFormat("The scheduled message was not found: {0}", exception.Message);
                        }

                        return;
                    }

                    await _observers.PreSend(context).ConfigureAwait(false);

                    var brokeredMessage = CreateBrokeredMessage(context);

                    if (context.ScheduledEnqueueTimeUtc.HasValue && context.ScheduledEnqueueTimeUtc.Value < DateTime.UtcNow)
                    {
                        var enqueueTimeUtc = context.ScheduledEnqueueTimeUtc.Value;

                        try
                        {
                            sequenceNumber = await clientContext.ScheduleSend(brokeredMessage, enqueueTimeUtc).ConfigureAwait(false);
                        }
                        catch (ArgumentOutOfRangeException exception)
                        {
                            brokeredMessage = CreateBrokeredMessage(context);

                            await clientContext.Send(brokeredMessage).ConfigureAwait(false);

                            sequenceNumber = 0;
                        }

                        context.SetScheduledMessageId(sequenceNumber);

                        context.LogScheduled(enqueueTimeUtc);
                    }
                    else
                    {
                        await clientContext.Send(brokeredMessage).ConfigureAwait(false);

                        context.LogSent();

                        await _observers.PostSend(context).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    await _observers.SendFault(context, ex).ConfigureAwait(false);

                    throw;
                }
            });

            return _source.Send(clientPipe, cancellationToken);
        }

        Message CreateBrokeredMessage<T>(AzureServiceBusSendContext<T> context)
            where T : class
        {
            var brokeredMessage = new Message(context.Body)
            {
                ContentType = context.ContentType.MediaType
            };

            brokeredMessage.UserProperties.SetTextHeaders(context.Headers, (_, text) => text);

            if (context.TimeToLive.HasValue)
                brokeredMessage.TimeToLive = context.TimeToLive.Value;

            if (context.MessageId.HasValue)
                brokeredMessage.MessageId = context.MessageId.Value.ToString("N");

            if (context.CorrelationId.HasValue)
                brokeredMessage.CorrelationId = context.CorrelationId.Value.ToString("N");

            CopyIncomingIdentifiersIfPresent(context);
            if (context.PartitionKey != null)
                brokeredMessage.PartitionKey = context.PartitionKey;

            var sessionId = string.IsNullOrWhiteSpace(context.SessionId) ? context.ConversationId?.ToString("N") : context.SessionId;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                brokeredMessage.SessionId = sessionId;

                if (context.ReplyToSessionId == null)
                    brokeredMessage.ReplyToSessionId = sessionId;
            }

            if (context.ReplyToSessionId != null)
                brokeredMessage.ReplyToSessionId = context.ReplyToSessionId;

            return brokeredMessage;
        }

        public ConnectHandle ConnectSendObserver(ISendObserver observer)
        {
            return _observers.Connect(observer);
        }

        void CopyIncomingIdentifiersIfPresent<T>(AzureServiceBusSendContext<T> sendContext)
            where T : class
        {
            if (sendContext.TryGetPayload<ConsumeContext>(out var consumeContext))
            {
                if (consumeContext.TryGetPayload<BrokeredMessageContext>(out var brokeredMessageContext))
                {
                    if (sendContext.SessionId == null && brokeredMessageContext.ReplyToSessionId != null)
                        sendContext.SessionId = brokeredMessageContext.ReplyToSessionId;

                    if (sendContext.SessionId == null && brokeredMessageContext.SessionId != null)
                        sendContext.SessionId = brokeredMessageContext.SessionId;

                    if (sendContext.PartitionKey == null && brokeredMessageContext.PartitionKey != null)
                        sendContext.PartitionKey = brokeredMessageContext.PartitionKey;
                }
            }
        }

        protected override Task StopSupervisor(StopSupervisorContext context)
        {
            if (_log.IsDebugEnabled)
                _log.DebugFormat("Stopping transport: {0}", _address);

            return base.StopSupervisor(context);
        }
    }
}