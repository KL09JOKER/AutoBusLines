using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Unity.Entities;

namespace AutoBusLines
{
    /// <summary>
    /// Represents a proposed transit line in Plan / Preview Mode.
    /// Allows players to inspect, configure, and toggle lines and stops before building.
    /// </summary>
    public class PlannedRoute
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("color")]
        public string Color { get; set; }

        [JsonProperty("lengthKm")]
        public float LengthKm { get; set; }

        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonProperty("stops")]
        public List<PlannedStopData> Stops { get; set; } = new List<PlannedStopData>();
    }

    /// <summary>
    /// Represents an individual stop location along a proposed line.
    /// Can be individually toggled by the player before network construction.
    /// </summary>
    public class PlannedStopData
    {
        [JsonProperty("index")]
        public int Index { get; set; }

        [JsonProperty("virtualId")]
        public int VirtualId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("posX")]
        public float PosX { get; set; }

        [JsonProperty("posY")]
        public float PosY { get; set; }

        [JsonProperty("posZ")]
        public float PosZ { get; set; }

        [JsonProperty("isStationBay")]
        public bool IsStationBay { get; set; }

        [JsonProperty("isPreExisting")]
        public bool IsPreExisting { get; set; }

        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonIgnore]
        public BusLineGenerator.PlacedStop InternalStop;
    }
}
