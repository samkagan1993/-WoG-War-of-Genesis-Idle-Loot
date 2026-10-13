using System;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace BlacksmithProbe
{
    [BepInPlugin("blacksmith.probe", "BlacksmithProbe", "1.2.0")]
    public class Plugin : BasePlugin
    {
        internal static ConfigEntry<bool> AutoCombineUnlock;
        internal static ConfigEntry<string> AutoCombineMaxRating;
        internal static ConfigEntry<bool> AutoCombineIncludeStorage;
        internal static ConfigEntry<bool> AutoReconnect;
        internal static ConfigEntry<int> AutoReconnectDelaySec;
        internal static ConfigEntry<int> AutoReconnectMax;
        internal static ConfigEntry<bool> AutoSortInventory;
        internal static ConfigEntry<int> AutoSortIntervalSec;
        internal static ConfigEntry<bool> AutoTraining;
        internal static ConfigEntry<long> AutoTrainingReserveGold;
        internal static ConfigEntry<int> AutoTrainingIntervalMs;
        internal static ConfigEntry<bool> CombineLog;
        internal static ConfigEntry<bool> DumpStages;

        public override void Load()
        {
            BindConfig();
            PatchScriptLoader();
        }

        void BindConfig()
        {
            AutoCombineUnlock = Config.Bind("Auto Combine", "UnlockHigherTier", true,
                "Raise the Auto Combine tier limit (the game default is Magic / tier 2). " +
                "Choose the tier in the in-game Auto Combine settings.");
            AutoCombineMaxRating = Config.Bind("Auto Combine", "MaxRating", "Myth",
                new ConfigDescription("Highest tier shown in the Auto Combine dropdown.",
                    new AcceptableValueList<string>("Magic", "Rare", "Hero", "Legend", "Myth")));
            AutoCombineIncludeStorage = Config.Bind("Auto Combine", "IncludeStorage", false,
                "true = Auto Combine also uses items in Storage. false = Inventory only (game default). " +
                "Locked, equipped and market-listed items are never used.");

            AutoReconnect = Config.Bind("Auto Reconnect", "Enabled", true,
                "When the reconnect-failed popup (Retry/Exit) appears, choose Retry automatically.");
            AutoReconnectDelaySec = Config.Bind("Auto Reconnect", "DelaySec", 15,
                new ConfigDescription("Delay before the first retry in seconds. Each following retry waits twice as long, up to 5 minutes.",
                    new AcceptableValueRange<int>(5, 300)));
            AutoReconnectMax = Config.Bind("Auto Reconnect", "MaxRetry", 20,
                new ConfigDescription("Maximum consecutive retries. After that the popup is shown as usual.",
                    new AcceptableValueRange<int>(1, 100)));

            AutoSortInventory = Config.Bind("Auto Sort", "Enabled", true,
                "Press Sort in the Inventory periodically. Note: the game's sort also clears the 'New' marker on items.");
            AutoSortIntervalSec = Config.Bind("Auto Sort", "IntervalSec", 60,
                new ConfigDescription("Delay between sorts in seconds. Minimum 10.",
                    new AcceptableValueRange<int>(10, 86400)));

            AutoTraining = Config.Bind("Auto Training", "Enabled", true,
                "Automatically level up Training whenever there is enough gold, always picking the cheapest first.");
            AutoTrainingReserveGold = Config.Bind("Auto Training", "ReserveGold", 0L,
                "Minimum gold to keep after a level up.");
            AutoTrainingIntervalMs = Config.Bind("Auto Training", "IntervalMs", 1500,
                new ConfigDescription("Delay between level ups in milliseconds. Minimum 1000, the same as the game's own auto delay.",
                    new AcceptableValueRange<int>(1000, 60000)));

            CombineLog = Config.Bind("Logging", "CombineLog", true,
                "Write every combine (auto and manual) to BepInEx/WoGAuto_Combine.log. " +
                "Auto Training is logged to BepInEx/WoGAuto_Training.log.");

            DumpStages = Config.Bind("Stage Export", "DumpStageTable", true,
                "Export the stage table (gold/exp per mob and boss) to BepInEx/WoGAuto_Stages.csv once per session.");
        }

        static void PatchScriptLoader()
        {
            var scriptLoader = AccessTools.TypeByName("NTSEnv+NTSLoader");
            var readFile = scriptLoader == null ? null : AccessTools.Method(scriptLoader, "ReadFile");
            if (readFile == null) return;
            try
            {
                var postfix = new HarmonyMethod(AccessTools.Method(typeof(ScriptPatcher), nameof(ScriptPatcher.ReadFilePostfix)));
                new Harmony("blacksmith.probe").Patch(readFile, postfix: postfix);
            }
            catch
            {
            }
        }
    }

    internal static class ScriptPatcher
    {
        static readonly Regex MaxRatingPattern = new Regex(@"AUTO_FUSION_MAX_RATING=E_RatingType\.\w+");

        const string CapRatingOriginal =
            "capAutoFusionRating(rating){return rating===E_RatingType.None||rating>AUTO_FUSION_MAX_RATING?AUTO_FUSION_MAX_RATING:rating}";
        const string CapRatingSafe =
            "capAutoFusionRating(rating){return rating===E_RatingType.None?E_RatingType.Magic:rating>AUTO_FUSION_MAX_RATING?AUTO_FUSION_MAX_RATING:rating}";

        const string StorageOriginal =
            "collectAutoRegisterCandidates(\"Fusion\",(itemTid,itemId)=>!this.isFusionStaged(itemId)&&this.canUseAsAutoFusionMaterial(contentType,itemTid,itemId),!1,{";
        const string StorageIncluded =
            "collectAutoRegisterCandidates(\"Fusion\",(itemTid,itemId)=>!this.isFusionStaged(itemId)&&this.canUseAsAutoFusionMaterial(contentType,itemTid,itemId),!0,{";

        const string ReconnectPopupOriginal =
            "popupData=PopupDataFactory.createSystemPopupWithOKCancel(nn.db.locale.getText(\"Net_Error_Fail_Reconnect\"),nn.db.locale.getText(\"Net_Error_Check_Connection\")),nn.ui.systemPopup.showUserSelection(popupData)";
        const string ReconnectPopupAuto =
            "popupData=PopupDataFactory.createSystemPopupWithOKCancel(nn.db.locale.getText(\"Net_Error_Fail_Reconnect\"),nn.db.locale.getText(\"Net_Error_Check_Connection\")),(globalThis.__wogReconnect?globalThis.__wogReconnect(popupData,E_PopupChoice.OK,p=>nn.ui.systemPopup.showUserSelection(p)):nn.ui.systemPopup.showUserSelection(popupData))";

        internal static void ReadFilePostfix(object[] __args, ref string __result)
        {
            try
            {
                if (string.IsNullOrEmpty(__result)) return;
                var path = __args != null && __args.Length > 0 ? __args[0]?.ToString() ?? "" : "";
                if (!path.Contains("main.bundle")) return;

                if (Plugin.AutoCombineUnlock.Value)
                    __result = UnlockAutoCombineTier(__result);
                if (Plugin.AutoCombineIncludeStorage.Value)
                    __result = ReplaceIfFound(__result, StorageOriginal, StorageIncluded);
                if (Plugin.AutoReconnect.Value)
                    __result = ReplaceIfFound(__result, ReconnectPopupOriginal, ReconnectPopupAuto);
                if (NeedsInjectedScript())
                    __result = __result + "\n" + BuildInjectedScript();
            }
            catch
            {
            }
        }

        static bool NeedsInjectedScript() =>
            Plugin.AutoTraining.Value || Plugin.CombineLog.Value || Plugin.DumpStages.Value ||
            Plugin.AutoSortInventory.Value || Plugin.AutoReconnect.Value;

        static string ReplaceIfFound(string source, string original, string replacement) =>
            source.Contains(original) ? source.Replace(original, replacement) : source;

        static string UnlockAutoCombineTier(string source)
        {
            if (!MaxRatingPattern.IsMatch(source) || !source.Contains(CapRatingOriginal)) return source;
            source = source.Replace(CapRatingOriginal, CapRatingSafe);
            return MaxRatingPattern.Replace(source,
                "AUTO_FUSION_MAX_RATING=E_RatingType." + Plugin.AutoCombineMaxRating.Value, 1);
        }

        static string BuildInjectedScript()
        {
            var root = Paths.BepInExRootPath.Replace('\\', '/');
            return InjectedScript
                .Replace("__TRAINING__", JsBool(Plugin.AutoTraining.Value))
                .Replace("__RESERVE__", Math.Max(0L, Plugin.AutoTrainingReserveGold.Value).ToString())
                .Replace("__INTERVAL__", Math.Max(1000, Plugin.AutoTrainingIntervalMs.Value).ToString())
                .Replace("__COMBINELOG__", JsBool(Plugin.CombineLog.Value))
                .Replace("__DUMPSTAGES__", JsBool(Plugin.DumpStages.Value))
                .Replace("__SORTINV__", JsBool(Plugin.AutoSortInventory.Value))
                .Replace("__SORTINTERVAL__", (Math.Max(10, Plugin.AutoSortIntervalSec.Value) * 1000).ToString())
                .Replace("__RECONNECT__", JsBool(Plugin.AutoReconnect.Value))
                .Replace("__RECONNECTDELAY__", (Math.Max(5, Plugin.AutoReconnectDelaySec.Value) * 1000).ToString())
                .Replace("__RECONNECTMAX__", Math.Max(1, Plugin.AutoReconnectMax.Value).ToString())
                .Replace("__TRAINING_LOG__", root + "/WoGAuto_Training.log")
                .Replace("__COMBINE_LOG__", root + "/WoGAuto_Combine.log")
                .Replace("__RECONNECT_LOG__", root + "/WoGAuto_Reconnect.log")
                .Replace("__STAGES_CSV__", root + "/WoGAuto_Stages.csv");
        }

        static string JsBool(bool value) => value ? "true" : "false";

        const string InjectedScript = @";(function () {
  var CFG = {
    training: __TRAINING__,
    reserve: __RESERVE__,
    interval: __INTERVAL__,
    combineLog: __COMBINELOG__,
    dumpStages: __DUMPSTAGES__,
    sortInventory: __SORTINV__,
    sortIntervalMs: __SORTINTERVAL__,
    autoReconnect: __RECONNECT__,
    reconnectDelayMs: __RECONNECTDELAY__,
    reconnectMax: __RECONNECTMAX__,
    reconnectLogPath: ""__RECONNECT_LOG__"",
    stagesPath: ""__STAGES_CSV__"",
    trainingLogPath: ""__TRAINING_LOG__"",
    combineLogPath: ""__COMBINE_LOG__""
  };

  var TREE_PASSIVE_SKILL = 1;
  var CONTENT_TRAINING = 13;
  var NET_SUCCESS = 1000;
  var MAX_LOG_LINES = 5000;
  var RECONNECT_MAX_DELAY_MS = 5 * 60 * 1000;
  var RECONNECT_RESET_MS = 10 * 60 * 1000;
  var RATING = [""None"", ""Normal"", ""Magic"", ""Rare"", ""Hero"", ""Legend"", ""Myth"", ""Ancient"", ""Primordial"", ""Transcendent"", ""Divine""];
  var AREA = [""None"", ""Normal"", ""Hard"", ""Nightmare"", ""Hell"", ""Inferno"", ""Chaos"", ""Abyss"", ""Transcendent"", ""Divine"", ""Eternal""];

  function pad(n) { return (n < 10 ? ""0"" : """") + n; }

  function timestamp() {
    var d = new Date();
    return d.getFullYear() + ""-"" + pad(d.getMonth() + 1) + ""-"" + pad(d.getDate()) + "" "" +
      pad(d.getHours()) + "":"" + pad(d.getMinutes()) + "":"" + pad(d.getSeconds());
  }

  function formatNumber(n) {
    var s = String(Math.floor(Number(n)));
    var negative = s.charAt(0) === ""-"";
    if (negative) s = s.substring(1);
    var out = """";
    while (s.length > 3) {
      out = "","" + s.substring(s.length - 3) + out;
      s = s.substring(0, s.length - 3);
    }
    return (negative ? ""-"" : """") + s + out;
  }

  function createFileLogger(path) {
    var lines = null;
    return function (line) {
      try {
        if (lines === null) {
          var existing = null;
          try { existing = CS.NTSHelper.ReadAllText(path); } catch (e) { }
          lines = existing ? String(existing).split(""\n"").filter(function (l) { return l.length > 0; }) : [];
        }
        lines.push(""["" + timestamp() + ""] "" + line);
        if (lines.length > MAX_LOG_LINES) lines.splice(0, lines.length - MAX_LOG_LINES);
        CS.NTSHelper.WriteAllText(path, lines.join(""\n"") + ""\n"");
      } catch (e) { }
    };
  }

  var logTraining = createFileLogger(CFG.trainingLogPath);
  var logCombine = createFileLogger(CFG.combineLogPath);
  var logReconnect = createFileLogger(CFG.reconnectLogPath);

  function game() { return globalThis.nn; }

  function isLoggedIn() {
    var nn = game();
    return !!(nn && nn.net && nn.net.data && nn.net.data.user && nn.net.data.user.isLoggedIn);
  }

  function services() {
    var nn = game();
    return nn && nn.services;
  }

  function itemName(tid) {
    try { return services().item.getItemName(tid) || (""#"" + tid); } catch (e) { return ""#"" + tid; }
  }

  function stackCount(tid) {
    try { return Number(services().item.getStackItemCount(tid)); } catch (e) { return NaN; }
  }

  function trainingName(tree, group) {
    var tid = tree.getCurrentEnchantTreeId(TREE_PASSIVE_SKILL, group);
    var table = tree.getTable(TREE_PASSIVE_SKILL, tid);
    try {
      if (table && table.Name) return game().db.locale.getText(table.Name) || (""Group "" + group);
    } catch (e) { }
    return ""Group "" + group;
  }

  function findCheapestTraining(tree) {
    var best = null;
    var bestCost = Infinity;
    var groups = tree.getGroupIds(TREE_PASSIVE_SKILL) || [];
    for (var i = 0; i < groups.length; i++) {
      var group = groups[i];
      if (!tree.canEnchantGroup(TREE_PASSIVE_SKILL, group)) continue;
      var tid = tree.getCurrentEnchantTreeId(TREE_PASSIVE_SKILL, group);
      var materials = tree.getLevelUpMaterials(TREE_PASSIVE_SKILL, tid) || [];
      var cost = 0;
      var keepsReserve = true;
      for (var j = 0; j < materials.length; j++) {
        cost += Number(materials[j].needCnt);
        if (Number(materials[j].haveCnt) - Number(materials[j].needCnt) < CFG.reserve) keepsReserve = false;
      }
      if (keepsReserve && cost < bestCost) {
        bestCost = cost;
        best = { group: group, materials: materials };
      }
    }
    return best;
  }

  async function runAutoTraining() {
    if (!CFG.training || !isLoggedIn()) return;
    var svc = services();
    var tree = svc && svc.tree;
    if (!tree || !svc.unlockCondition || svc.unlockCondition.isLocked(CONTENT_TRAINING)) return;

    var target = findCheapestTraining(tree);
    if (!target) return;

    var name = trainingName(tree, target.group);
    var levelBefore = tree.getReachedLevel(TREE_PASSIVE_SKILL, target.group);
    var costs = target.materials.map(function (m) {
      return { tid: m.itemTid, need: Number(m.needCnt), have: stackCount(m.itemTid) };
    });

    if (!await tree.reqEnchantAsync(TREE_PASSIVE_SKILL, target.group)) return;

    var levelAfter = tree.getReachedLevel(TREE_PASSIVE_SKILL, target.group);
    var effect = """";
    try {
      var reached = tree.getReachedTable(TREE_PASSIVE_SKILL, target.group);
      if (reached) effect = svc.ability.getAbilitiesEffectText(reached.AbilityType, reached.AbilityDetailCnt);
    } catch (e) { }

    var costText = costs.map(function (c) {
      return itemName(c.tid) + "" "" + formatNumber(c.have) + "" -> "" + formatNumber(stackCount(c.tid)) + "" (-"" + formatNumber(c.need) + "")"";
    }).join("", "");

    logTraining(name + "" Lv."" + levelBefore + "" -> Lv."" + levelAfter +
      (effect ? "" ["" + String(effect).replace(/\n/g, "" "") + ""]"" : """") + "" | "" + costText);
  }

  function describeItem(workshop, itemTid, itemId, count) {
    var info = null;
    try { info = workshop.getTableInfo(itemTid, itemId || 0n); } catch (e) { }
    var text = itemName(itemTid);
    if (info) {
      var level = info.limitLevel != null ? info.limitLevel : info.baseLimitLevel;
      text += "" (T"" + info.ratingType + "" "" + (RATING[info.ratingType] || """") + "", Lv."" + level + "")"";
    }
    return count > 1 ? text + "" x"" + count : text;
  }

  function hookCombineLog() {
    var svc = services();
    var workshop = svc && svc.workshop;
    if (!workshop || workshop.__wogHooked || typeof workshop.reqFusionAsync !== ""function"") return;

    var original = workshop.reqFusionAsync;
    workshop.reqFusionAsync = async function (fusionTid, materials) {
      var source = this.isAutoFusionRunning ? ""[AUTO]"" : ""[MANUAL]"";
      var materialText = """";
      try {
        materialText = (materials || []).map(function (m) {
          return describeItem(workshop, m.itemTid, m.itemId, Number(m.itemCnt || 1));
        }).join("", "");
      } catch (e) { }

      var res = await original.apply(this, arguments);
      if (!CFG.combineLog) return res;

      try {
        if (!res || res.NetResult !== NET_SUCCESS) {
          logCombine(source + "" REQUEST ERROR (NetResult="" + (res ? res.NetResult : ""null"") + "") | Materials: "" + materialText);
          return res;
        }
        var fusion = game().db.fusion.get(fusionTid);
        var contentType = fusion ? fusion.ContentType : 0;
        var rewards = res.Data ? this.buildFusionRewards(res.Data, contentType) : [];
        var main = rewards[0];
        var resultText = ""NO RESULT"";
        if (main) {
          var success = false;
          try { success = this.isFusionResultSuccess(fusionTid, main.tid); } catch (e) { }
          resultText = (success ? ""SUCCESS"" : ""FAILED"") + "" -> "" + describeItem(this, main.tid, 0n, 1);
        }
        var bonus = rewards.slice(1).map(function (r) { return itemName(r.tid) + "" x"" + r.cntOrLevel; }).join("", "");
        logCombine(source + "" "" + resultText + "" | Materials: "" + materialText + (bonus ? "" | Bonus: "" + bonus : """"));
      } catch (e) { }
      return res;
    };
    workshop.__wogHooked = true;
  }

  var stagesExported = false;

  function csvValue(v) {
    if (v == null) return """";
    if (Array.isArray(v)) v = v.join(""|"");
    v = String(v);
    return /["",\n]/.test(v) ? '""' + v.replace(/""/g, '""""') + '""' : v;
  }

  function exportStageTable() {
    if (stagesExported || !CFG.dumpStages || !isLoggedIn()) return;
    var nn = game();
    var stages = nn.db && nn.db.stage && nn.db.stage.container;
    if (!stages || !stages.size) return;

    var rows = [""StageID,Area,AreaType,StageType,GroupID,StageStep,Step,Name,MobSpawnCnt,ClearGoldReward,ClearExpReward,BossKillGold,BossKillExp,DropCoolTime,DropRate,NextStageID""];
    stages.forEach(function (t, id) {
      var name = """";
      try { if (t.DungeonName) name = nn.db.locale.getText(t.DungeonName) || """"; } catch (e) { }
      rows.push([id, AREA[t.AreaType] || t.AreaType, t.AreaType, t.StageType, t.GroupID, t.StageStep, t.Step, name,
        t.MobSpawnCnt, t.ClearGoldReward, t.ClearExpReward, t.BossKillGold, t.BossKillExp,
        t.DropCoolTime, t.DropRate, t.NextStageID].map(csvValue).join("",""));
    });

    try { CS.NTSHelper.WriteAllText(CFG.stagesPath, rows.join(""\n"") + ""\n""); } catch (e) { }
    stagesExported = true;
  }

  var lastSortAt = Date.now();

  async function runAutoSort() {
    if (!CFG.sortInventory || Date.now() - lastSortAt < CFG.sortIntervalMs || !isLoggedIn()) return;
    var svc = services();
    var itemMove = svc && svc.itemMove;
    if (!itemMove || typeof itemMove.sortInventory !== ""function"" || itemMove._bItemRequestBusy) return;
    lastSortAt = Date.now();
    await itemMove.sortInventory();
  }

  var reconnectAttempt = 0;
  var lastReconnectAt = 0;

  globalThis.__wogReconnect = function (popupData, retryChoice, showPopup) {
    if (!CFG.autoReconnect) return showPopup(popupData);

    var now = Date.now();
    if (now - lastReconnectAt > RECONNECT_RESET_MS) reconnectAttempt = 0;
    lastReconnectAt = now;
    reconnectAttempt++;

    if (reconnectAttempt > CFG.reconnectMax) {
      logReconnect(""Failed "" + CFG.reconnectMax + "" times in a row. Auto reconnect stopped, showing popup."");
      reconnectAttempt = 0;
      return showPopup(popupData);
    }

    var delay = Math.min(CFG.reconnectDelayMs * Math.pow(2, reconnectAttempt - 1), RECONNECT_MAX_DELAY_MS);
    logReconnect(""Disconnected. Auto retry #"" + reconnectAttempt + "" in "" + Math.round(delay / 1000) + "" seconds."");
    return new Promise(function (resolve) {
      setTimeout(function () { resolve(retryChoice); }, delay);
    });
  };

  var busy = false;

  async function tick() {
    if (busy) return;
    busy = true;
    try {
      hookCombineLog();
      exportStageTable();
      await runAutoTraining();
      await runAutoSort();
    } catch (e) {
    } finally {
      busy = false;
    }
  }

  setInterval(tick, CFG.interval);
})();
";
    }
}