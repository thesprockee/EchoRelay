﻿using Newtonsoft.Json;

namespace EchoRelay.Core.Monitoring;

public class GameServerObject
{
    public string serverIP { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string region { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string level { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string gameMode { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int playerCount { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool assigned { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string sessionID { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ulong gameServerID { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool @public { get; set; }
}