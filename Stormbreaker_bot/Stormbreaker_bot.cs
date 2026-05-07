using cAlgo.API;
using System;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class Stormbreaker_bot : Robot
    {
        [Parameter("API URL", DefaultValue = "http://localhost:8000/signal")]
        public string ApiUrl { get; set; }

        [Parameter("Bot Label", DefaultValue = "STORMBREAKER")]
        public string BotLabel { get; set; }

        [Parameter("Max Daily Trades", DefaultValue = 20, MinValue = 1)]
        public int MaxDailyTrades { get; set; }

        [Parameter("Min Confidence", DefaultValue = 88, MinValue = 0, MaxValue = 100)]
        public int MinConfidence { get; set; }

        [Parameter("Default Lot", DefaultValue = 0.01, MinValue = 0.01, Step = 0.01)]
        public double DefaultLot { get; set; }

        [Parameter("Max Lot", DefaultValue = 0.05, MinValue = 0.01, Step = 0.01)]
        public double MaxLot { get; set; }

        [Parameter("Stop Loss (pips)", DefaultValue = 200, MinValue = 1)]
        public int StopLossPips { get; set; }

        [Parameter("Take Profit (pips)", DefaultValue = 400, MinValue = 1)]
        public int TakeProfitPips { get; set; }

        [Parameter("Prevent Same Direction Duplicates", DefaultValue = true)]
        public bool PreventDuplicateDirection { get; set; }

        [Parameter("Cooldown (minutes)", DefaultValue = 5, MinValue = 0)]
        public int CooldownMinutes { get; set; }

        private int _tradesToday;
        private DateTime _lastResetDate;
        private DateTime _lastTradeTimeUtc;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        protected override void OnStart()
        {
            _tradesToday = 0;
            _lastResetDate = DateTime.UtcNow.Date;
            _lastTradeTimeUtc = DateTime.MinValue;

            Print("✅ Stormbreaker bot restored and upgraded.");
            Print($"Symbol={SymbolName}, MaxDailyTrades={MaxDailyTrades}, MinConfidence={MinConfidence}%");
        }

        protected override void OnBar()
        {
            ResetDailyCounterIfNeeded();

            if (_tradesToday >= MaxDailyTrades)
            {
                Print("⏸ Daily trade limit reached.");
                return;
            }

            if (CooldownMinutes > 0 && _lastTradeTimeUtc != DateTime.MinValue)
            {
                var elapsed = DateTime.UtcNow - _lastTradeTimeUtc;
                if (elapsed.TotalMinutes < CooldownMinutes)
                    return;
            }

            var response = GetSignal(SymbolName);
            if (response == null)
                return;

            var signal = NormalizeSignal(response.Signal);
            if (signal == "HOLD")
                return;

            if (response.Confidence < MinConfidence)
                return;

            var tradeType = signal == "BUY" ? TradeType.Buy : TradeType.Sell;

            if (PreventDuplicateDirection && HasOpenPositionInSameDirection(tradeType))
            {
                Print($"🔁 Duplicate prevented: {tradeType} position already open.");
                return;
            }

            ExecuteTrade(tradeType, response.Lot, response.Confidence);
        }

        private void ResetDailyCounterIfNeeded()
        {
            var today = DateTime.UtcNow.Date;
            if (today <= _lastResetDate)
                return;

            _tradesToday = 0;
            _lastResetDate = today;
            Print("📅 New UTC day detected. Daily trade counter reset.");
        }

        private AIResponse GetSignal(string symbol)
        {
            if (string.IsNullOrWhiteSpace(ApiUrl))
            {
                Print("❌ API URL is empty.");
                return null;
            }

            try
            {
                using var client = new WebClient();
                client.Headers[HttpRequestHeader.Accept] = "application/json";

                var url = $"{ApiUrl}?symbol={Uri.EscapeDataString(symbol)}";
                var json = client.DownloadString(url);

                if (string.IsNullOrWhiteSpace(json))
                {
                    Print("⚠ API returned empty response.");
                    return null;
                }

                return JsonSerializer.Deserialize<AIResponse>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                Print("❌ API error: " + ex.Message);
                return null;
            }
        }

        private void ExecuteTrade(TradeType tradeType, double apiLot, int confidence)
        {
            var requestedLot = apiLot > 0 ? apiLot : DefaultLot;
            var finalLot = Math.Min(requestedLot, MaxLot);

            var volumeInUnits = Symbol.QuantityToVolumeInUnits(finalLot);
            volumeInUnits = Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);

            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                Print($"⚠ Volume too small after normalization. Lot={finalLot}");
                return;
            }

            var result = ExecuteMarketOrder(
                tradeType,
                SymbolName,
                volumeInUnits,
                string.IsNullOrWhiteSpace(BotLabel) ? "STORMBREAKER" : BotLabel,
                StopLossPips,
                TakeProfitPips);

            if (!result.IsSuccessful)
            {
                Print($"❌ Trade failed: {result.Error}");
                return;
            }

            _tradesToday++;
            _lastTradeTimeUtc = DateTime.UtcNow;

            Print($"📈 TRADE EXECUTED: {tradeType} | Conf={confidence}% | Lot={finalLot} | Volume={volumeInUnits}");
        }

        private bool HasOpenPositionInSameDirection(TradeType tradeType)
        {
            foreach (var position in Positions)
            {
                if (position.SymbolName != SymbolName)
                    continue;

                if (position.TradeType != tradeType)
                    continue;

                if (!string.IsNullOrWhiteSpace(BotLabel) && position.Label != BotLabel)
                    continue;

                return true;
            }

            return false;
        }

        private static string NormalizeSignal(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "HOLD";

            raw = raw.Trim().ToUpperInvariant();

            return raw switch
            {
                "BUY" => "BUY",
                "SELL" => "SELL",
                _ => "HOLD"
            };
        }

        public class AIResponse
        {
            [JsonPropertyName("symbol")]
            public string Symbol { get; set; }

            [JsonPropertyName("signal")]
            public string Signal { get; set; }

            [JsonPropertyName("confidence")]
            public int Confidence { get; set; }

            [JsonPropertyName("lot")]
            public double Lot { get; set; }
        }
    }
}
