﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Bitnet.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;


namespace CentralMine.NET
{
    public class ClientManager
    {
        public enum Currency
        {
            Bitcoin,
            Feathercoin,
            Novacoin
        };

        Dictionary<uint, bool> mBlacklist;
        Dictionary<Currency, string> mCurrencyProviders;

        Listener mListener;
        public List<Client> mClients;
        Mutex mClientListMutex;

        Thread mUpdateThread;
        Email mMailer;
        
        public double mHashrate;
        int mPrevBlockIndex;
        public Block[] mPrevBlocks;
        public Block mBlock = null;

        public bool mDumpClients = false;
        public Currency mCurrency;
        
        public ClientManager()
        {
            mBlacklist = new Dictionary<uint, bool>();
            mBlacklist[0xC425E50F] = true;

            mCurrency = Currency.Bitcoin;
            mCurrencyProviders = new Dictionary<Currency, string>();
            mCurrencyProviders[Currency.Bitcoin] = "http://127.0.0.1:8332";
            mCurrencyProviders[Currency.Feathercoin] = "http://127.0.0.1:9667";
            mCurrencyProviders[Currency.Novacoin] = "http://127.0.0.1:7332";

            mPrevBlocks = new Block[5];
            mPrevBlockIndex = 0;

            mMailer = new Email();
            mClients = new List<Client>();
            mClientListMutex = new Mutex();
            mListener = new Listener(80, this);

            mUpdateThread = new Thread(new ThreadStart(Update));

            mHashrate = 0;
            BeginBlock();

            mUpdateThread.Start();
        }

        public void SetCurrency(Currency c)
        {
            if (mCurrency != c)
            {
                mCurrency = c;
            }
        }

        public void AcceptClient(TcpClient client)
        {
            IPEndPoint ep = client.Client.RemoteEndPoint as IPEndPoint;
            byte[] bytes = ep.Address.GetAddressBytes();
            uint addr = (uint)(bytes[0] << 24) | (uint)(bytes[1] << 16) | (uint)(bytes[2] << 8) | bytes[3];
            if (!mBlacklist.ContainsKey(addr))
            {
                Client c = new Client(client, this);
                mClientListMutex.WaitOne();
                mClients.Add(c);
                mClientListMutex.ReleaseMutex();
            }
        }

        void BeginBlock()
        {
            JObject obj = null;
            try
            {
                // Get block from bitcoin
                BitnetClient bc = new BitnetClient(mCurrencyProviders[mCurrency]);
                bc.Credentials = new NetworkCredential("rpcuser", "rpcpass");
                obj = bc.GetWork();
            }
            catch (Exception e)
            {
                Console.WriteLine("Failed to get work!");
                Console.WriteLine(e.Message);
            }

            if (obj != null)
            {

                // Put the current block in the previous list
                if (mBlock != null)
                {
                    mPrevBlocks[mPrevBlockIndex++] = mBlock;
                    if (mPrevBlockIndex >= mPrevBlocks.Length)
                        mPrevBlockIndex = 0;
                }

                Console.WriteLine("starting " + mCurrency + " block: " + obj.ToString());
                mBlock = new Block(obj);
                mBlock.mCurrency = mCurrency;
            }
        }

        void AssignWork(Client c)
        {
            if (mBlock != null)
            {
                HashManager.HashBlock hashes = mBlock.mHashMan.Allocate(c.mDesiredHashes, c);
                if (hashes != null)
                    c.SendWork(hashes, mBlock);
            }
        }

        public void WorkComplete(Client solver, bool solutionFound, uint solution)
        {
            Block block = solver.mCurrentBlock;
            block.mHashMan.FinishBlock(solver.mHashBlock);
            solver.mHashBlock = null;

            if (solutionFound)
            {
                // Submit this solution to bitcoin
                string data = block.GetSolutionString(solution);
                Console.WriteLine("Trying solution: " + data);
                BitnetClient bc = new BitnetClient(mCurrencyProviders[block.mCurrency]);
                bc.Credentials = new NetworkCredential("rpcuser", "rpcpass");
                bool success = bc.GetWork(data);
                if (!success)
                {
                    data = block.GetSolutionString((uint)IPAddress.HostToNetworkOrder((int)solution));
                    success = bc.GetWork(data);
                }

                // Send email notification about this found solution
                TimeSpan span = DateTime.Now - block.mHashMan.mStartTime;
                string hashrate = string.Format("{0:N}", block.mHashMan.mHashesDone / span.TotalSeconds);
                string body = "Found solution for " + block.mCurrency + " block: \n" + block.ToString() + "\n\n";
                body += "Solution string: " + data + "\n";
                body += "Block Accepted: " + success.ToString() + "\n";
                body += "Hashes Done: " + block.mHashMan.mHashesDone + "\n";
                body += "Time Spent: " + span.ToString() + "\n";
                body += "Hashrate: " + hashrate + "\n";
                body += "Clients: " + mClients.Count + "\n";
                body += "\n\n";
                mMailer.SendEmail(body);

                // Start a new block
                if( success )
                    BeginBlock();
            }
        }

        public uint GetHashesDone()
        {
            uint hashesDone = mBlock.mHashMan.mHashesDone;
            foreach (Block b in mPrevBlocks)
            {
                if (b != null)
                    hashesDone += b.mHashMan.mHashesDone;
            }
            return hashesDone;
        }

        void Update()
        {
            while (true)
            {
                double oldHashrate = mHashrate;                
                mHashrate = 0;
                if (mBlock == null || mBlock.mHashMan.IsComplete() || mBlock.mHashMan.IsExpired())
                {
                    // Start work on a new block
                    BeginBlock();
                }

                Client clientThatWantsInfo = null;

                if (mClientListMutex.WaitOne(1))
                {
                    int clientsOnline = mClients.Count;
                    foreach (Client c in mClients)
                    {
                        bool stillAlive = c.Update();
                        if (!stillAlive)
                        {
                            if (c.mCurrentBlock != null)
                                c.mCurrentBlock.mHashMan.FreeBlock(c.mHashBlock);
                            mClients.Remove(c);
                            break;
                        }
                        mHashrate += c.mHashrate;

                        if (c.mState == Client.State.Ready)
                            AssignWork(c);

                        if (c.mStatusClient)
                            c.SendStatus(clientsOnline, oldHashrate);

                        if (c.mClientInfoRequested && clientThatWantsInfo == null)
                        {
                            clientThatWantsInfo = c;
                            c.mClientInfoRequested = false;
                        }
                    }

                    if (clientThatWantsInfo != null)
                    {
                        string info = "{\"clients\":[";
                        foreach (Client c in mClients)
                        {
                            info += c.ToJSON() + ",";
                        }
                        info = info.Substring(0, info.Length - 1);
                        info += "]}";
                        clientThatWantsInfo.SendClientInfo(info);
                    }

                    mClientListMutex.ReleaseMutex();
                    Thread.Sleep(50);
                }
                else
                {
                    Thread.Sleep(1);
                }

                if (mDumpClients)
                {
                    FileStream file = new FileStream("clients.txt", FileMode.Create);
                    StreamWriter sw = new StreamWriter(file);

                    mClientListMutex.WaitOne();
                    foreach (Client c in mClients)
                    {
                        string str = c.ToString();
                        sw.WriteLine(str);
                    }
                    mClientListMutex.ReleaseMutex();

                    sw.Close();
                    mDumpClients = false;
                }
            }
        }
    }
}