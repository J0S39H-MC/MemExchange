﻿using System;
using System.Collections.Generic;
using MemExchange.Core.SharedDto.ClientToServer;
using MemExchange.Core.SharedDto.Orders;
using MemExchange.Server.Common;
using MemExchange.Server.Incoming;
using MemExchange.Server.Outgoing;
using MemExchange.Server.Processor.Book;

namespace MemExchange.Server.Processor
{
    public class IncomingMessageProcessor : IIncomingMessageProcessor
    {
        private readonly IOrderRepository ordeRepository;
        private readonly IOutgoingQueue outgoingQueue;
        private readonly IDateService dateService;
        private readonly IOrderDispatcher dispatcher;

        public IncomingMessageProcessor(IOrderRepository ordeRepository, IOutgoingQueue outgoingQueue, IDateService dateService, IOrderDispatcher dispatcher)
        {
            this.ordeRepository = ordeRepository;
            this.outgoingQueue = outgoingQueue;
            this.dateService = dateService;
            this.dispatcher = dispatcher;
        }

        public void OnNext(ClientToServerMessageQueueItem data, long sequence, bool endOfBatch)
        {
            data.StartProcessTime = dateService.UtcNow();

            switch (data.Message.MessageType)
            {
               case ClientToServerMessageTypeEnum.ModifyStopLimitOrder:
                    if (data.Message.ClientId <= 0)
                        break;

                    var stopLimitOrderToModify = ordeRepository.TryGetStopLimitOrder(data.Message.StopLimitOrder.ExchangeOrderId);
                    if (stopLimitOrderToModify == null)
                        return;

                    stopLimitOrderToModify.Modify(data.Message.StopLimitOrder.TriggerPrice, data.Message.StopLimitOrder.LimitPrice, data.Message.StopLimitOrder.Quantity);
                    outgoingQueue.EnqueueUpdatedStopLimitOrder(stopLimitOrderToModify);

                    break;

               case ClientToServerMessageTypeEnum.RequestOpenStopLimitOrders:
                    if (data.Message.ClientId <= 0)
                        break;

                    var orders = ordeRepository.GetClientStopLimitOrders(data.Message.ClientId);
                    if (orders.Count == 0)
                        return;

                    outgoingQueue.EnqueueStopLimitOrderSnapshot(data.Message.ClientId, orders);
                    break;

                case ClientToServerMessageTypeEnum.CancelStopLimitOrder:
                    var stopOrderToCancel = ordeRepository.TryGetStopLimitOrder(data.Message.StopLimitOrder.ExchangeOrderId);

                    if (stopOrderToCancel != null)
                    {
                        stopOrderToCancel.Delete();
                        outgoingQueue.EnqueueDeletedStopLimitOrder(stopOrderToCancel);
                    }
                    break;

                case ClientToServerMessageTypeEnum.PlaceStopLimitOrder:
                    if (!data.Message.StopLimitOrder.ValidateForAdd())
                        return;

                    var newStopLimitOrder = ordeRepository.NewStopLimitOrder(data.Message.StopLimitOrder);
                    dispatcher.HandleAddStopLimitOrder(newStopLimitOrder);
                    break;


                case ClientToServerMessageTypeEnum.PlaceMarketOrder:
                    if (!data.Message.MarketOrder.ValidateForExecute())
                        return;

                    var newMarketOrder = ordeRepository.NewMarketOrder(data.Message.MarketOrder);
                    dispatcher.HandleMarketOrder(newMarketOrder);
                    break;

                case ClientToServerMessageTypeEnum.PlaceLimitOrder:
                    if (!data.Message.LimitOrder.ValidatesForAdd())
                    {
                        outgoingQueue.EnqueueMessage(data.Message.ClientId, "Error: Limit order was rejected.");
                        break;
                    }

                    var newLimitOrder = ordeRepository.NewLimitOrder(data.Message.LimitOrder);
                    newLimitOrder.RegisterDeleteNotificationHandler(outgoingQueue.EnqueueDeletedLimitOrder);
                    newLimitOrder.RegisterModifyNotificationHandler(outgoingQueue.EnqueueUpdatedLimitOrder);
                    newLimitOrder.RegisterFilledNotification(outgoingQueue.EnqueueDeletedLimitOrder);
                    newLimitOrder.RegisterFilledNotification((order) => order.Delete());

                    dispatcher.HandleAddLimitOrder(newLimitOrder);
                break;

                case ClientToServerMessageTypeEnum.CancelLimitOrder:
                if (!data.Message.LimitOrder.ValidateForDelete())
                    {
                        outgoingQueue.EnqueueMessage(data.Message.ClientId, "Error: Cancellation of limit order was rejected.");
                        break;
                    }

                    var orderToDelete = ordeRepository.TryGetLimitOrder(data.Message.LimitOrder.ExchangeOrderId);
                    if (orderToDelete != null)
                    {
                        orderToDelete.Delete();
                        outgoingQueue.EnqueueDeletedLimitOrder(orderToDelete);
                    }
                    break;

                case ClientToServerMessageTypeEnum.ModifyLimitOrder:
                    if (!data.Message.LimitOrder.ValidatesForModify())
                    {
                        outgoingQueue.EnqueueMessage(data.Message.ClientId, "Error: Modification of limit order was rejected.");
                        break;
                    }

                    var orderToModify = ordeRepository.TryGetLimitOrder(data.Message.LimitOrder.ExchangeOrderId);
                    if (orderToModify != null)
                        orderToModify.Modify(data.Message.LimitOrder.Quantity, data.Message.LimitOrder.Price);
                    break;

                case ClientToServerMessageTypeEnum.RequestOpenLimitOrders:
                    if (data.Message.ClientId <= 0)
                        break;

                    var orderList = ordeRepository.GetClientStopLimitOrders(data.Message.ClientId);
                    outgoingQueue.EnqueueStopLimitOrderSnapshot(data.Message.ClientId, orderList);
                    break;
            }

            data.Message.Reset();
        }
    }
}