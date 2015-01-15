using System;
using System.Collections.Generic;
using UnityEngine;
using DarkMultiPlayerCommon;
using MessageStream2;

namespace DarkMultiPlayer
{
    public class WarpWorker
    {
        public bool workerEnabled = false;
        public WarpMode warpMode = WarpMode.SUBSPACE;
        //Private parts
        private static WarpWorker singleton;
        //A list of lowest rates in MCW_LOWEST mode.
        private Dictionary<string, PlayerWarpRate> clientWarpList = new Dictionary<string, PlayerWarpRate>();
        //Read from DebugWindow
        public Dictionary<string, float> clientSkewList = new Dictionary<string, float>();
        //A list of the subspaces that all the clients belong to.
        private Dictionary<string, int> clientSubspaceList = new Dictionary<string, int>();
        //Warp state tracking
        private PlayerWarpRate lastSendRate = new PlayerWarpRate();
        //MCW states
        private string warpMaster = "";
        private string voteMaster = "";
        private double controllerExpireTime = double.NegativeInfinity;
        private double voteExpireTime = double.NegativeInfinity;
        private int voteYesCount;
        private int voteNoCount;
        private bool voteSent;
        private double lastScreenMessageCheck;
        //Report tracking
        private double lastWarpSet;
        private double lastReportRate;
        private Queue<byte[]> newWarpMessages = new Queue<byte[]>();
        private ScreenMessage warpMessage;
        private const float SCREEN_MESSAGE_UPDATE_INTERVAL = 0.2f;
        private const float WARP_SET_THROTTLE = 1f;
        private const float REPORT_SKEW_RATE_INTERVAL = 10f;
        private const float RELEASE_AFTER_WARP_TIME = 10f;
        //MCW Succeed/Fail counts.
        private int voteNeededCount
        {
            get
            {
                return (PlayerStatusWorker.fetch.GetPlayerCount() + 1) / 2;
            }
        }
        private int voteFailedCount
        {
            get
            {

                return voteNeededCount + (1 - (voteNeededCount % 2));
            }
        }

        public static WarpWorker fetch
        {
            get
            {
                return singleton;
            }
        }

        private void Update()
        {
            if (workerEnabled)
            {
                CheckWarp();

                //Process new warp messages
                ProcessWarpMessages();

                //Write the screen message if needed
                if ((UnityEngine.Time.realtimeSinceStartup - lastScreenMessageCheck) > SCREEN_MESSAGE_UPDATE_INTERVAL)
                {
                    lastScreenMessageCheck = UnityEngine.Time.realtimeSinceStartup;
                    UpdateScreenMessage();
                }
                //Send a CHANGE_WARP message if needed
                if ((warpMode == WarpMode.MCW_FORCE) || (warpMode == WarpMode.MCW_VOTE) || (warpMode == WarpMode.SUBSPACE))
                {
                    if ((lastSendRate.rateIndex != TimeWarp.CurrentRateIndex) || (lastSendRate.isPhysWarp != (TimeWarp.WarpMode == TimeWarp.Modes.LOW)))
                    {
                        lastSendRate.isPhysWarp = (TimeWarp.WarpMode == TimeWarp.Modes.LOW);
                        lastSendRate.rateIndex = TimeWarp.CurrentRateIndex;
                        using (MessageWriter mw = new MessageWriter())
                        {
                            mw.Write<int>((int)WarpMessageType.CHANGE_WARP);
                            mw.Write<bool>(lastSendRate.isPhysWarp);
                            mw.Write<int>(lastSendRate.rateIndex);
                            mw.Write<long>(TimeSyncer.fetch.GetServerClock());
                            mw.Write<double>(Planetarium.GetUniversalTime());
                            NetworkWorker.fetch.SendWarpMessage(mw.GetMessageBytes());
                        }
                    }
                }

                if (warpMode == WarpMode.MCW_FORCE || warpMode == WarpMode.MCW_VOTE)
                {
                    //Follow the warp master into warp if needed (MCW_FORCE/MCW_VOTE)
                    if ((UnityEngine.Time.realtimeSinceStartup - lastWarpSet) > WARP_SET_THROTTLE)
                    {
                        //The warp master isn't us
                        if ((warpMaster != Settings.fetch.playerName) && (warpMaster != ""))
                        {
                            //We have a entry for the warp master
                            if (clientWarpList.ContainsKey(warpMaster))
                            {
                                PlayerWarpRate masterWarpRate = clientWarpList[warpMaster];
                                //Our warp rate is different

                                float[] warpRates = null;
                                if (masterWarpRate.isPhysWarp)
                                {
                                    TimeWarp.fetch.Mode = TimeWarp.Modes.LOW;
                                    warpRates = TimeWarp.fetch.physicsWarpRates;
                                }
                                else
                                {
                                    TimeWarp.fetch.Mode = TimeWarp.Modes.HIGH;
                                    warpRates = TimeWarp.fetch.warpRates;
                                }

                                if (TimeWarp.CurrentRateIndex != masterWarpRate.rateIndex)
                                {
                                    lastWarpSet = UnityEngine.Time.realtimeSinceStartup;
                                    long serverClockDiff = TimeSyncer.fetch.GetServerClock() - masterWarpRate.serverClock;
                                    double secondsDiff = serverClockDiff / 10000000d;
                                    double newTime = masterWarpRate.planetTime + (warpRates[masterWarpRate.rateIndex] * secondsDiff);
                                    Planetarium.SetUniversalTime(newTime);
                                    TimeWarp.SetRate(masterWarpRate.rateIndex, true);
                                }
                            }
                        }
                    }
                }
                //Set the warp rate to the lowest rate if needed (MCW_LOWEST)
                if (warpMode == WarpMode.MCW_LOWEST)
                {
                    int lowestPhysRateIndex = -1;
                    int lowestNormalRateIndex = -1;
                    foreach (KeyValuePair<string, PlayerWarpRate> pwr in clientWarpList)
                    {
                        if (pwr.Value.isPhysWarp)
                        {
                            if (lowestPhysRateIndex == -1)
                            {
                                lowestPhysRateIndex = pwr.Value.rateIndex;
                            }
                            if (lowestPhysRateIndex > pwr.Value.rateIndex)
                            {
                                lowestPhysRateIndex = pwr.Value.rateIndex;
                            }
                        }
                        else
                        {
                            if (lowestNormalRateIndex == -1)
                            {
                                lowestNormalRateIndex = pwr.Value.rateIndex;
                            }
                            if (lowestNormalRateIndex > pwr.Value.rateIndex)
                            {
                                lowestNormalRateIndex = pwr.Value.rateIndex;
                            }
                        }
                    }
                    if (lowestNormalRateIndex > 0 && lowestPhysRateIndex == -1)
                    {
                        TimeWarp.fetch.Mode = TimeWarp.Modes.HIGH;
                        if (TimeWarp.CurrentRateIndex != lowestNormalRateIndex)
                        {
                            TimeWarp.SetRate(lowestNormalRateIndex, true);
                        }
                    }
                    else if (lowestPhysRateIndex > 0 && lowestNormalRateIndex == -1)
                    {
                        TimeWarp.fetch.Mode = TimeWarp.Modes.LOW;
                        if (TimeWarp.CurrentRateIndex != lowestPhysRateIndex)
                        {
                            TimeWarp.SetRate(lowestNormalRateIndex, true);
                        }
                    }
                }
                //Report our timeSyncer skew
                if ((UnityEngine.Time.realtimeSinceStartup - lastReportRate) > REPORT_SKEW_RATE_INTERVAL && TimeSyncer.fetch.locked)
                {
                    lastReportRate = UnityEngine.Time.realtimeSinceStartup;
                    using (MessageWriter mw = new MessageWriter())
                    {
                        mw.Write<int>((int)WarpMessageType.REPORT_RATE);
                        mw.Write<float>(TimeSyncer.fetch.requestedRate);
                        NetworkWorker.fetch.SendWarpMessage(mw.GetMessageBytes());
                    }
                }
                //Handle warp keys
                HandleInput();
            }
        }

        public void ProcessWarpMessages()
        {
            while (newWarpMessages.Count > 0)
            {
                HandleWarpMessage(newWarpMessages.Dequeue());
            }
        }

        private void UpdateScreenMessage()
        {
            if (warpMaster != "")
            {
                if (warpMaster != Settings.fetch.playerName)
                {
                    DisplayMessage(warpMaster + " currently has warp control", 1f);
                }
                else
                {
                    int timeLeft = (int)(controllerExpireTime - UnityEngine.Time.realtimeSinceStartup);
                    DisplayMessage("You have warp control, press '<' while not in warp to release (timeout " + timeLeft + "s)", 1f);
                }
            }
            else
            {
                if (voteMaster != "")
                {
                    if (voteMaster == Settings.fetch.playerName)
                    {
                        int timeLeft = (int)(voteExpireTime - UnityEngine.Time.realtimeSinceStartup);
                        DisplayMessage("Waiting for vote replies... Yes: " + voteYesCount + ", No: " + voteNoCount + ", Needed: " + voteNeededCount + " (" + timeLeft + " left)", 1f);
                    }
                    else
                    {
                        if (voteSent)
                        {
                            DisplayMessage("Voted!", 1f);
                        }
                        else
                        {
                            DisplayMessage(voteMaster + " has started a warp vote, reply with '<' for no or '>' for yes", 1f);
                        }
                    }
                }
            }
        }

        private void CheckWarp()
        {
            bool resetWarp = true;
            if ((warpMode == WarpMode.MCW_FORCE) || (warpMode == WarpMode.MCW_VOTE))
            {
                if (warpMaster != "")
                {
                    //It could be us or another player. If it's another player it will be controlled from Update() instead.
                    resetWarp = false;
                }
            }
            if (warpMode == WarpMode.SUBSPACE)
            {
                //Never reset warp in SUBSPACE mode.
                resetWarp = false;
            }
            if (warpMode == WarpMode.MCW_LOWEST)
            {
                //Controlled from above in Update()
                resetWarp = false;
            }
            if ((TimeWarp.CurrentRateIndex > 0) && resetWarp)
            {
                DarkLog.Debug("Resetting warp rate back to 0");
                TimeWarp.SetRate(0, true);
            }
            if ((TimeWarp.CurrentRateIndex > 0) && (TimeWarp.CurrentRate > 1.1f) && !resetWarp && TimeSyncer.fetch.locked)
            {
                DarkLog.Debug("Unlocking from subspace");
                TimeSyncer.fetch.UnlockSubspace();
            }
            if ((TimeWarp.CurrentRateIndex == 0) && (TimeWarp.CurrentRate < 1.1f) && !TimeSyncer.fetch.locked && (warpMode == WarpMode.SUBSPACE) && (TimeSyncer.fetch.currentSubspace == -1))
            {
                SendNewSubspace();
            }
        }

        public static void SendNewSubspace()
        {
            SendNewSubspace(TimeSyncer.fetch.GetServerClock(), Planetarium.GetUniversalTime(), TimeSyncer.fetch.requestedRate);
        }

        public static void SendNewSubspace(long serverClock, double planetTime, float subspaceRate)
        {
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<int>((int)WarpMessageType.NEW_SUBSPACE);
                mw.Write<long>(serverClock);
                mw.Write<double>(planetTime);
                mw.Write<float>(subspaceRate);
                NetworkWorker.fetch.SendWarpMessage(mw.GetMessageBytes());
            }
        }

        private void HandleInput()
        {
            bool startWarpKey = Input.GetKeyDown(KeyCode.Period);
            bool stopWarpKey = Input.GetKeyDown(KeyCode.Comma);
            if (startWarpKey || stopWarpKey)
            {
                switch (warpMode)
                {
                    case WarpMode.NONE:
                        DisplayMessage("Cannot warp, warping is disabled on this server", 5f);
                        break;
                    case WarpMode.MCW_FORCE:
                        HandleMCWForceInput(startWarpKey, stopWarpKey);
                        break;
                    case WarpMode.MCW_VOTE:
                        HandleMCWVoteInput(startWarpKey, stopWarpKey);
                        break;
                }
            }
        }

        private void HandleMCWForceInput(bool startWarpKey, bool stopWarpKey)
        {
            if (warpMaster == "")
            {
                if (startWarpKey)
                {
                    using (MessageWriter mw = new MessageWriter())
                    {
                        mw.Write<int>((int)WarpMessageType.REQUEST_CONTROLLER);
                        NetworkWorker.fetch.SendWarpMessage(mw.GetMessageBytes());
                    }
                }
            }
            else if (warpMaster == Settings.fetch.playerName)
            {
                if (stopWarpKey && (TimeWarp.CurrentRate < 1.1f))
                {
                    ReleaseWarpMaster();
                }
            }
        }

        private void HandleMCWVoteInput(bool startWarpKey, bool stopWarpKey)
        {
            if (warpMaster == "")
            {
                if (voteMaster == "")
                {
                    if (startWarpKey)
                    {
                        //Start a warp vote
                        using (MessageWriter mw = new MessageWriter())
                        {
                            mw.Write<int>((int)WarpMessageType.REQUEST_CONTROLLER);
                            NetworkWorker.fetch.SendWarpMessage(mw.GetMessageBytes());
                        }
                        //TODO
                        //voteMaster = Settings.fetch.playerName;
                        //To win:
                        //1 other clients = 1 vote needed.
                        //2 other clients = 1 vote needed.
                        //3 other clients = 2 votes neeed.
                        //4 other clients = 2 votes neeed.
                        //5 other clients = 3 votes neeed.
                        //To fail:
                        //1 other clients = 1 vote needed.
                        //2 other clients = 2 vote needed.
                        //3 other clients = 2 votes neeed.
                        //4 other clients = 3 votes neeed.
                        //5 other clients = 3 votes neeed.
                        //voteNeededCount = (PlayerStatusWorker.fetch.playerStatusList.Count + 1) / 2;
                        //voteFailedCount = voteNeededCount + (1 - (voteNeededCount % 2));
                        //DarkLog.Debug("Started warp vote");
                        //Nobody else is online, Let's just take the warp master.
                        //warpMasterOwnerTime = UnityEngine.Time.realtimeSinceStartup;
                        //warpMaster = Settings.fetch.playerName;
                        /*
                            using (MessageWriter mw = new MessageWriter())
                            {
                                mw.Write<int>((int)WarpMessageType.RELEASE_CONTROLLER);
                                mw.Write<string>(Settings.fetch.playerName);
                                mw.Write<string>(Settings.fetch.playerName);
                                NetworkWorker.fetch.SendWarpMessage(mw.GetMessageBytes());
                            }
                            */
                    }
                }
                else
                {
                    if (voteMaster != Settings.fetch.playerName)
                    {
                        //Send a vote if we haven't voted yet
                        if (!voteSent)
                        {
                            using (MessageWriter mw = new MessageWriter())
                            {
                                mw.Write<int>((int)WarpMessageType.REPLY_VOTE);
                                mw.Write<string>(Settings.fetch.playerName);
                                mw.Write<bool>(startWarpKey);
                                NetworkWorker.fetch.SendWarpMessage(mw.GetMessageBytes());
                                voteSent = true;
                            }
                            DarkLog.Debug("Send warp reply with vote of " + startWarpKey);
                        }
                    }
                    else
                    {
                        if (stopWarpKey)
                        {
                            //Cancel our vote
                            ReleaseWarpMaster();
                            DisplayMessage("Cancelled vote!", 2f);
                        }
                    }
                }
            }
            else
            {
                if (warpMaster == Settings.fetch.playerName)
                {
                    if (stopWarpKey && (TimeWarp.CurrentRate < 1.1f))
                    {
                        //Release control of the warp master instead of waiting for the timeout
                        ReleaseWarpMaster();
                    }
                }
            }
        }

        private void ReleaseWarpMaster()
        {
            if (warpMaster == Settings.fetch.playerName)
            {
                SendNewSubspace();
            }
            warpMaster = "";
            voteSent = false;
            voteMaster = "";
            voteYesCount = 0;
            voteNoCount = 0;
            controllerExpireTime = double.NegativeInfinity;
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<int>((int)WarpMessageType.RELEASE_CONTROLLER);
                NetworkWorker.fetch.SendWarpMessage(mw.GetMessageBytes());
            }
            if (TimeWarp.CurrentRateIndex > 0)
            {
                DarkLog.Debug("Resetting warp rate back to 0");
                TimeWarp.SetRate(0, true);
            }
        }

        private void HandleWarpMessage(byte[] messageData)
        {
            using (MessageReader mr = new MessageReader(messageData))
            {
                WarpMessageType messageType = (WarpMessageType)mr.Read<int>();
                switch (messageType)
                {
                    case WarpMessageType.REQUEST_VOTE:
                        {
                            voteMaster = mr.Read<string>();
                            long expireTime = mr.Read<long>();
                            voteExpireTime = Time.realtimeSinceStartup + ((expireTime - TimeSyncer.fetch.GetServerClock()) / 10000000d);
                        }
                        break;
                    case WarpMessageType.REPLY_VOTE:
                        {
                            voteYesCount = mr.Read<int>();
                            voteNoCount = mr.Read<int>();
                        }
                        break;
                    case WarpMessageType.SET_CONTROLLER:
                        {
                            string newController = mr.Read<string>();
                            long expireTime = mr.Read<long>();
                            HandleSetController(newController, expireTime);
                        }
                        break;
                    case WarpMessageType.CHANGE_WARP:
                        {
                            string fromPlayer = mr.Read<string>();
                            bool isPhysWarp = mr.Read<bool>();
                            int rateIndex = mr.Read<int>();
                            long serverClock = mr.Read<long>();
                            double planetTime = mr.Read<double>();
                            HandleChangeWarp(fromPlayer, isPhysWarp, rateIndex, serverClock, planetTime);
                        }
                        break;
                    case WarpMessageType.NEW_SUBSPACE:
                        {
                            int newSubspaceID = mr.Read<int>();
                            long serverTime = mr.Read<long>();
                            double planetariumTime = mr.Read<double>();
                            float gameSpeed = mr.Read<float>();
                            TimeSyncer.fetch.LockNewSubspace(newSubspaceID, serverTime, planetariumTime, gameSpeed);
                        }
                        break;
                    case WarpMessageType.CHANGE_SUBSPACE:
                        {
                            string fromPlayer = mr.Read<string>();
                            if (fromPlayer != Settings.fetch.playerName)
                            {
                                int changeSubspaceID = mr.Read<int>();
                                clientSubspaceList[fromPlayer] = changeSubspaceID;
                            }
                        }
                        break;
                    case WarpMessageType.RELOCK_SUBSPACE:
                        {
                            int subspaceID = mr.Read<int>();
                            long serverTime = mr.Read<long>();
                            double planetariumTime = mr.Read<double>();
                            float gameSpeed = mr.Read<float>();
                            TimeSyncer.fetch.RelockSubspace(subspaceID, serverTime, planetariumTime, gameSpeed);
                        }
                        break;
                    case WarpMessageType.REPORT_RATE:
                        {
                            string fromPlayer = mr.Read<string>();
                            clientSkewList[fromPlayer] = mr.Read<float>();
                        }
                        break;
                    default:
                        {
                            DarkLog.Debug("Unhandled WARP_MESSAGE type: " + messageType);
                            break;
                        }
                }
            }
        }

        private void HandleReplyVote(int voteYesCount, int voteNoCount)
        {
            if (warpMode == WarpMode.MCW_VOTE)
            {
                this.voteYesCount = voteYesCount;
                this.voteNoCount = voteNoCount;

                if (voteMaster == Settings.fetch.playerName && warpMaster == "")
                {
                    //We have enough votes
                    if (voteNoCount >= voteFailedCount)
                    {
                        //Vote has failed.
                        ReleaseWarpMaster();
                        DisplayMessage("Vote failed!", 5f);
                    }
                }
            }
        }

        private void HandleSetController(string newController, long expireTime)
        {
            if (warpMode == WarpMode.MCW_FORCE || warpMode == WarpMode.MCW_VOTE || warpMode == WarpMode.MCW_LOWEST)
            {
                warpMaster = newController;
                if (warpMaster == "")
                {
                    voteMaster = "";
                    voteYesCount = 0;
                    voteNoCount = 0;
                    controllerExpireTime = double.NegativeInfinity;
                    TimeWarp.SetRate(0, true);
                }
                else
                {
                    long expireTimeDelta = expireTime - TimeSyncer.fetch.GetServerClock();
                    controllerExpireTime = Time.realtimeSinceStartup + (expireTimeDelta / 10000000d);

                }
            }
        }

        private void HandleChangeWarp(string fromPlayer, bool isPhysWarp, int newRate, long serverClock, double planetTime)
        {
            if (warpMode == WarpMode.MCW_FORCE || warpMode == WarpMode.MCW_VOTE || warpMode == WarpMode.MCW_LOWEST || warpMode == WarpMode.SUBSPACE)
            {
                if (clientWarpList.ContainsKey(fromPlayer))
                {
                    clientWarpList[fromPlayer].isPhysWarp = isPhysWarp;
                    clientWarpList[fromPlayer].rateIndex = newRate;
                }
                else
                {
                    PlayerWarpRate newPlayerWarpRate = new PlayerWarpRate();
                    newPlayerWarpRate.isPhysWarp = isPhysWarp;
                    newPlayerWarpRate.rateIndex = newRate;
                    newPlayerWarpRate.serverClock = serverClock;
                    newPlayerWarpRate.planetTime = planetTime;
                    clientWarpList.Add(fromPlayer, newPlayerWarpRate);
                }
                //DarkLog.Debug(fromPlayer + " warp rate changed, Physwarp: " + newPhysWarp + ", Index: " + newRateIndex);
            }
        }

        private void DisplayMessage(string messageText, float messageDuration)
        {
            if (warpMessage != null)
            {
                warpMessage.duration = 0f;
            }
            warpMessage = ScreenMessages.PostScreenMessage(messageText, messageDuration, ScreenMessageStyle.UPPER_CENTER);
        }

        public void QueueWarpMessage(byte[] messageData)
        {
            newWarpMessages.Enqueue(messageData);
        }

        public int GetClientSubspace(string playerName)
        {
            return clientSubspaceList.ContainsKey(playerName) ? clientSubspaceList[playerName] : -1;
        }

        public List<int> GetActiveSubspaces()
        {
            List<int> returnList = new List<int>();
            returnList.Add(TimeSyncer.fetch.currentSubspace);
            foreach (KeyValuePair<string, int> clientSubspace in clientSubspaceList)
            {
                if (!returnList.Contains(clientSubspace.Value))
                {
                    returnList.Add(clientSubspace.Value);
                }
            }
            returnList.Sort(subspaceComparer);
            return returnList;
        }

        private int subspaceComparer(int lhs, int rhs)
        {
            double subspace1Time = TimeSyncer.fetch.GetUniverseTime(lhs);
            double subspace2Time = TimeSyncer.fetch.GetUniverseTime(rhs);
            //x<y -1, x==y 0, x>y 1
            if (subspace1Time < subspace2Time)
            {
                return -1;
            }
            if (subspace1Time == subspace2Time)
            {
                return 0;
            }
            return 1;
        }

        public List<string> GetClientsInSubspace(int subspace)
        {
            List<string> returnList = new List<string>();
            //Add other players
            foreach (KeyValuePair<string, int> clientSubspace in clientSubspaceList)
            {
                if (clientSubspace.Value == subspace)
                {
                    returnList.Add(clientSubspace.Key);
                }
            }
            returnList.Sort();
            //Add us if we are in the subspace
            if (TimeSyncer.fetch.currentSubspace == subspace)
            {
                //We are on top!
                returnList.Insert(0, Settings.fetch.playerName);
            }
            return returnList;
        }

        public void RemovePlayer(string playerName)
        {
            if (clientSubspaceList.ContainsKey(playerName))
            {
                clientSubspaceList.Remove(playerName);
            }
            if (clientSkewList.ContainsKey(playerName))
            {
                clientSkewList.Remove(playerName);
            }
            if (clientWarpList.ContainsKey(playerName))
            {
                clientWarpList.Remove(playerName);
            }
        }

        public static void Reset()
        {
            lock (Client.eventLock)
            {
                if (singleton != null)
                {
                    singleton.workerEnabled = false;
                    Client.updateEvent.Remove(singleton.Update);
                }
                singleton = new WarpWorker();
                Client.updateEvent.Add(singleton.Update);
            }
        }
    }

    public class PlayerWarpRate
    {
        public bool isPhysWarp = false;
        public int rateIndex = 0;
        public long serverClock = 0;
        public double planetTime = 0;
    }
}

