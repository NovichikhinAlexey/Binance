﻿using Newtonsoft.Json;

namespace Binance
{
    public sealed class MarginAccountBalance
    {
        [JsonProperty("asset")]
        public string Asset { get; set; }

        [JsonProperty("free")]
        public decimal Free { get; set; }

        [JsonProperty("locked")]
        public decimal Locked { get; set; }

        [JsonProperty("interest")]
        public decimal Interest { get; set; }

        [JsonProperty("netAsset")]
        public decimal NetAsset { get; set; }
    }
}