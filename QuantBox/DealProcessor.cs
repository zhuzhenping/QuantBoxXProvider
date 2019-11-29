﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Dataflow;
using QuantBox.XApi;
using SmartQuant;
using ExecType = SmartQuant.ExecType;
using OrderStatus = SmartQuant.OrderStatus;

namespace QuantBox
{
    internal class DealProcessor
    {
        private struct TradingEvent
        {
            public readonly ExecType Type;
            public readonly Order Order;
            public readonly TradeField Trade;
            public readonly OrderField OrderReturn;

            public TradingEvent(ExecType type, Order order)
            {
                Type = type;
                Order = order;
                Trade = null;
                OrderReturn = null;
            }

            public TradingEvent(ExecType type, TradeField trade)
            {
                Type = type;
                Trade = trade;
                Order = null;
                OrderReturn = null;
            }

            public TradingEvent(ExecType type, OrderField order)
            {
                Type = type;
                OrderReturn = order;
                Trade = null;
                Order = null;
            }
        }

        private readonly XProvider _provider;
        private readonly OrderMap _map = new OrderMap();
        private ActionBlock<TradingEvent> _orderBlock;
        private delegate void OrderReturnHandler(OrderField field);
        private readonly IdArray<OrderReturnHandler> _orderHandlers = new IdArray<OrderReturnHandler>(byte.MaxValue);

        private OrderField CreateOrderField(Order order)
        {
            var field = new OrderField();
            var (symbol, exchange) = _provider.GetSymbolInfo(order.Instrument);
            field.InstrumentID = symbol;
            field.ExchangeID = exchange;
            field.Price = order.Price;
            field.Qty = order.Qty;
            field.TimeInForce = (XApi.TimeInForce)order.TimeInForce;
            field.Type = (XApi.OrderType)order.Type;
            field.HedgeFlag = Convertor.GetHedgeFlag(order, _provider.DefaultHedgeFlag);
            field.Side = Convertor.GetSide(order);
            field.OpenClose = order.GetOpenClose();
            if (field.OpenClose == OpenCloseType.Open
                && field.Side == XApi.OrderSide.Sell
                && order.SubSide == SubSide.Undefined) {
                order.SetSubSide(SubSide.SellShort);
            }
            field.ClientID = order.ClientID;
            field.AccountID = order.Account;
            field.StopPx = order.StopPx;
            field.ID = GetOrderId(order);
            field.LocalID = GetOrderLocalId(order);
            if (!string.IsNullOrEmpty(order.ProviderOrderId)) {
                field.OrderID = order.ProviderOrderId;
                field.ExchangeID = order.Instrument.Exchange;
                field.Status = XApi.OrderStatus.New;
            }
            else {
                field.Status = XApi.OrderStatus.NotSent;
            }
            return field;
        }

        private static ExecutionReport CreateReport(OrderRecord record, OrderStatus ordStatus, ExecType execType, string text = "")
        {
            var report = new ExecutionReport(record.Order);
            report.DateTime = DateTime.Now;
            report.AvgPx = record.AvgPx;
            report.CumQty = record.CumQty;
            report.LeavesQty = record.LeavesQty;
             report.OrdStatus = ordStatus;
            report.ExecType = execType;
            report.Text = text == "" ? record.Order.Text : text;
            return report;
        }

        #region ProcessOrderReturn

        private void InitHandler()
        {
            void DefaultHandler(OrderField field)
            {
            }

            for (var i = 0; i < byte.MaxValue; i++) {
                _orderHandlers[i] = DefaultHandler;
            }
            _orderHandlers[(byte)ExecType.ExecNew] = ProcessExecNew;
            _orderHandlers[(byte)ExecType.ExecCancelled] = ProcessExecCancelled;
            _orderHandlers[(byte)ExecType.ExecRejected] = ProcessExecRejected;
            _orderHandlers[(byte)ExecType.ExecPendingCancel] = ProcessExecPendingCancel;
            _orderHandlers[(byte)ExecType.ExecCancelReject] = ProcessExecCancelReject;
        }

        private void ProcessReturnOrder(OrderField field)
        {
            _provider.Logger.Info(field.DebugInfo);
            _orderHandlers[(byte)field.ExecType](field);
        }

        private void ProcessExecCancelled(OrderField field)
        {
            if (_map.TryGetOrder(field.ID, out var record)) {
                _provider.OnMessage(CreateReport(record, (OrderStatus)field.Status, ExecType.ExecCancelled, field.Text()));
            }
        }

        private void ProcessExecNew(OrderField field)
        {
            if (_map.TryGetOrder(field.ID, out var record)) {
                if (string.IsNullOrEmpty(record.Order.ProviderOrderId)) {
                    _map.RemoveNoSend(field.ID);
                    record.Order.ProviderOrderId = field.OrderID;
                    _provider.OnMessage(CreateReport(record, (OrderStatus)field.Status, ExecType.ExecNew));
                }
            }
        }

        private void ProcessExecRejected(OrderField field)
        {
            if (_map.TryGetOrder(field.ID, out var record)) {
                var report = CreateReport(record, (OrderStatus)field.Status, ExecType.ExecRejected, field.Text());
                report.SetErrorId(field.XErrorID, field.RawErrorID);
                _provider.OnMessage(report);
            }
        }

        private void ProcessExecPendingCancel(OrderField field)
        {
            if (_map.TryGetOrder(field.ID, out var record)) {
                _provider.OnMessage(CreateReport(record, (OrderStatus)field.Status, ExecType.ExecPendingCancel));
            }
        }

        private void ProcessExecCancelReject(OrderField field)
        {
            if (_map.TryGetOrder(field.ID, out var record)) {
                _provider.OnMessage(CreateReport(record, (OrderStatus)field.Status, ExecType.ExecCancelReject,
                    field.Text()));
            }
        }

        private void ProcessTrade(TradeField trade)
        {
            _map.TryGetOrder(trade.ID, out var record);
            if (record != null) {
                record.AddFill(trade.Price, trade.Qty);
                var status = record.LeavesQty > 0 ? OrderStatus.PartiallyFilled : OrderStatus.Filled;
                var report = CreateReport(record, status, ExecType.ExecTrade);
                report.ExecId = trade.TradeID;
                report.DateTime = trade.UpdateTime();
                if (report.DateTime.Date != _provider.Trader.TradingDay) {
                    report.TransactTime = _provider.Trader.TradingDay.Add(report.DateTime.TimeOfDay);
                }
                else {
                    report.TransactTime = report.DateTime;
                }

                report.LastPx = trade.Price;
                report.LastQty = trade.Qty;

                if (Math.Abs(trade.Commission) < double.Epsilon) {
                    report.Commission = _provider.GetCommission(report);
                }
                else {
                    report.Commission = trade.Commission;
                }

                _provider.OnMessage(report);
                if (status == OrderStatus.Filled) {
                    _map.RemoveDone(trade.ID);
                }
            }
        }

        #endregion

        #region ProcessCommand

        private void OrderEventAction(TradingEvent e)
        {
            try {
                switch (e.Type) {
                    case ExecType.ExecNew:
                        ProcessSend(e.Order);
                        break;
                    case ExecType.ExecTrade:
                        ProcessTrade(e.Trade);
                        break;
                    case ExecType.ExecCancelled:
                        ProcessCancel(e.Order);
                        break;
                    case ExecType.ExecOrderStatus:
                        ProcessReturnOrder(e.OrderReturn);
                        break;
                }
            }
            catch (Exception ex) {
                _provider.OnProviderError(-1, ex.Message);
            }
        }

        private void ProcessCancel(Order order)
        {
            string error;
            if (_map.OrderExist(GetOrderId(order))) {
                error = _provider.Trader.CancelOrder(GetOrderId(order));
            }
            else {
                error = @"Can't Found Order";
            }

            if (!string.IsNullOrEmpty(error)) {
                _provider.OnMessage(CreateReport(new OrderRecord(order), order.Status, ExecType.ExecCancelReject,
                    error));
            }
        }

        private void ProcessSend(Order order)
        {
            _provider.Logger.Info(string.Join(",", order.Id, order.Side, order.Instrument.Symbol, order.Qty, order.Price));
            _map.AddNewOrder(GetOrderId(order), order);
            _provider.Trader.SendOrder(CreateOrderField(order));
        }

        #endregion

        public DealProcessor(XProvider provider)
        {
            _provider = provider;
            InitHandler();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static string GetOrderLocalId(Order order)
        {
            var id = GetOrderId(order);
            return (id.Length == 8) ? id : id.Substring(id.Length - 8, 8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static string GetOrderId(Order order)
        {
            return order.ClOrderId;
        }

        internal static string GetOrderId(ExecutionCommand order)
        {
            return order.ClOrderId;
        }

        public void ProcessNoSendOrders()
        {
            var list = _map.GetNoSend();
            list.Sort((x, y) => string.Compare(GetOrderId(x.Order), GetOrderId(y.Order), StringComparison.Ordinal));
            foreach (var record in list) {
                _provider.Trader.SendOrder(CreateOrderField(record.Order));
                _provider.SetOrderIdBase(int.Parse(GetOrderLocalId(record.Order)));
            }
        }

        public void LoadUndoneOrders(Framework framework, DateTime tradingDay, HashSet<string> processedTrades)
        {
            LoadOrders();
            ProcessMissed();
            SendToTrader();

            void LoadOrders()
            {
                foreach (var order in framework.OrderManager.Orders) {
                    if (order.ProviderId == _provider.Id
                        && !order.IsDone
                        && order.TransactTime > tradingDay) {
                        _map.AddNewOrder(GetOrderId(order), order);
                    }
                }
            }

            void ProcessMissed()
            {
                var data = framework.StrategyManager;
                var idMap = new Dictionary<string, string>();
                foreach (var field in data.GetExOrders(_provider.Id)) {
                    if (!string.IsNullOrEmpty(field.OrderID)) {
                        idMap[field.ID] = field.OrderID;
                        if (field.ExecType == XApi.ExecType.Trade) {
                            ProcessExecNew(field);
                        }
                    }
                    ProcessReturnOrder(field);
                }
                data.RemoveExOrders(_provider.Id);
                foreach (var trade in data.GetExTrades(_provider.Id)) {
                    if (!processedTrades.Contains($"{trade.TradeID}_{trade.Side}") && idMap.TryGetValue(trade.ID, out var id)) {
                        trade.ID = id;
                        ProcessTrade(trade);
                    }
                }
                data.RemoveExTrades(_provider.Id);
            }

            void SendToTrader()
            {
                foreach (var record in _map) {
                    if (!string.IsNullOrEmpty(record.Order.ProviderOrderId)) {
                        _provider.Trader.SendOrder(CreateOrderField(record.Order));
                    }
                }
            }
        }

        public void Open()
        {
            if (_orderBlock == null) {
                _orderBlock = new ActionBlock<TradingEvent>((Action<TradingEvent>)OrderEventAction);
            }
        }

        public void Close()
        {
            if (_orderBlock == null)
                return;
            _orderBlock.Complete();
            _orderBlock.Completion.Wait();
            _orderBlock = null;
        }

        public void PostNewOrder(Order order)
        {
            _orderBlock.Post(new TradingEvent(ExecType.ExecNew, order));
        }

        public void PostCancelOrder(Order order)
        {
            _orderBlock.Post(new TradingEvent(ExecType.ExecCancelled, order));
        }

        public void PostTrade(TradeField trade)
        {
            _orderBlock.Post(new TradingEvent(ExecType.ExecTrade, trade));
        }

        public void PostReturnOrder(OrderField order)
        {
            _orderBlock.Post(new TradingEvent(ExecType.ExecOrderStatus, order));
        }
    }
}
