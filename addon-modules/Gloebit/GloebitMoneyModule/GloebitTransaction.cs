/*
 * Copyright (c) 2015 Gloebit LLC
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

/*
 * GloebitTransaction.cs
 * 
 * Object representation of a Transaction for use with the GloebitAPI
 * See GloebitTransactionData.cs for DB implementation
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using log4net;
using OpenMetaverse;

namespace Gloebit.GloebitMoneyModule {

    public class GloebitTransaction {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // Primary Key value
        public UUID TransactionID;

        // Common, vital transaction details
        public UUID PayerID;
        public string PayerName;    // TODO: do we need to ensure this is not larger than the db field on hypergrid? - VARCHAR(255)
        public UUID PayeeID;
        public string PayeeName;    // TODO: do we need to ensure this is not larger than the db field on hypergrid? - VARCHAR(255)
        public int Amount;

        // Transaction classification info
        public int TransactionType;
        public string TransactionTypeString;

        // Subscription info
        public bool IsSubscriptionDebit;
        public UUID SubscriptionID;

        // Object info required when enacting/consume/canceling, delivering, and handling subscriptions
        public UUID PartID;         // UUID of object
        public string PartName;     // object name
        public string PartDescription;

        // Details required by IBuySellModule when delivering an object
        public UUID CategoryID;     // Appears to be a folder id used when saleType is copy
        private uint? m_localID;    // Region specific ID of object.  Unclear why this is passed instead of UUID
        public int SaleType;        // object, copy, or contents

        // Storage of submission/response from Gloebit
        public bool Submitted;
        public bool ResponseReceived;
        public bool ResponseSuccess;
        public string ResponseStatus;
        public string ResponseReason;
        public int PayerEndingBalance; // balance returned by transact when fully successful.

        // State variables used internally in GloebitAPI
        public bool enacted;
        public bool consumed;
        public bool canceled;

        // Timestamps for reporting
        public DateTime cTime;
        public DateTime? enactedTime;
        public DateTime? finishedTime;

        private static Dictionary<string, GloebitTransaction> s_transactionMap = new Dictionary<string, GloebitTransaction>();
        private static Dictionary<string, GloebitTransaction> s_pendingTransactionMap = new Dictionary<string, GloebitTransaction>(); // tracks assets currently being worked on so that two state functions are not enacted at the same time.

        public interface IAssetCallback {
            bool processAssetEnactHold(GloebitTransaction txn, out string returnMsg);
            bool processAssetConsumeHold(GloebitTransaction txn, out string returnMsg);
            bool processAssetCancelHold(GloebitTransaction txn, out string returnMsg);
        }

        // Necessary for use with standard db serialization system
        // See Create() to generate a new transaction record
        // See Get() to retrieve an existing transaction record
        public GloebitTransaction() {
            m_localID = null;
        }

        private GloebitTransaction(UUID transactionID, UUID payerID, string payerName, UUID payeeID, string payeeName, int amount, int transactionType, string transactionTypeString, bool isSubscriptionDebit, UUID subscriptionID, UUID partID, string partName, string partDescription, UUID categoryID, uint localID, int saleType) {

            // Primary Key value
            this.TransactionID = transactionID;

            // Common, vital transaction details
            this.PayerID = payerID;
            this.PayerName = payerName;
            this.PayeeID = payeeID;
            this.PayeeName = payeeName;
            this.Amount = amount;

            // Transaction classification info
            this.TransactionType = transactionType;
            this.TransactionTypeString = transactionTypeString;

            // Subscription info
            this.IsSubscriptionDebit = isSubscriptionDebit;
            this.SubscriptionID = subscriptionID;

            // Storage of submission/response from Gloebit
            this.Submitted = false;
            this.ResponseReceived = false;
            this.ResponseSuccess = false;
            this.ResponseStatus = String.Empty;
            this.ResponseReason = String.Empty;
            this.PayerEndingBalance = -1;


            // Object info required when enacting/consume/canceling, delivering, and handling subscriptions
            this.PartID = partID;
            this.PartName = partName;
            this.PartDescription = partDescription;

            // Details required by IBuySellModule when delivering an object
            this.CategoryID = categoryID;
            this.m_localID = localID;
            this.SaleType = saleType;

            // State variables used internally in GloebitAPI
            this.enacted = false;
            this.consumed = false;
            this.canceled = false;

            // Timestamps for reporting
            this.cTime = DateTime.UtcNow;
            this.enactedTime = null; // set to null instead of DateTime.MinValue to avoid crash on reading 0 timestamp
            this.finishedTime = null; // set to null instead of DateTime.MinValue to avoid crash on reading 0 timestamp
            // TODO: We have made these nullable and initialize to null.  We could alternatively choose a time that is not zero
            // and avoid any potential conficts from allowing null.
            // On MySql, I had to set the columns to allow NULL, otherwise, inserting null defaulted to the current local time.
            // On PGSql, I set the columns to allow NULL, but haven't tested.
            // On SQLite, I don't think that you can set them to allow NULL explicitely, and haven't checked defaults.
        }

        // Creates a new transaction
        // First verifies that a transaction with this ID does not already exist
        // --- If existing txn is found, returns null
        // Creates new Transaction, stores it in the cache and db
        public static GloebitTransaction Create(UUID transactionID, UUID payerID, string payerName, UUID payeeID, string payeeName, int amount, int transactionType, string transactionTypeString, bool isSubscriptionDebit, UUID subscriptionID, UUID partID, string partName, string partDescription, UUID categoryID, uint localID, int saleType)
        {
            // Create the Transaction
            GloebitTransaction txn = new GloebitTransaction(transactionID, payerID, payerName, payeeID, payeeName, amount, transactionType, transactionTypeString, isSubscriptionDebit, subscriptionID, partID, partName, partDescription, categoryID, localID, saleType);

            // Ensure that a transaction does not already exist with this ID before storing it
            string transactionIDstr = transactionID.ToString();
            GloebitTransaction existingTxn = Get(transactionIDstr);
            if (existingTxn != null) {
                // Record in DB store with this id -- return null
                return null;
            }
            // lock cache and ensure there is still no existing record before storing this txn.
            lock(s_transactionMap) {
                if (s_transactionMap.TryGetValue(transactionIDstr, out existingTxn)) {
                    return null;
                } else {
                    // Store the Transaction in the fast access cache
                    s_transactionMap[transactionIDstr] = txn;
                }
            }

            // Store the Transaction to the persistent DB
            GloebitTransactionData.Instance.Store(txn);

            return txn;
        }

        public bool TryGetLocalID(out uint localID) {
            if (m_localID != null) {
                localID = (uint)m_localID;
                return true;
            }
            localID = 0;
            return false;
        }

        public static GloebitTransaction Get(UUID transactionID) {
            return Get(transactionID.ToString());
        }

        public static GloebitTransaction Get(string transactionIDStr) {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] in Transaction.Get");
            GloebitTransaction transaction = null;
            lock(s_transactionMap) {
                s_transactionMap.TryGetValue(transactionIDStr, out transaction);
            }

            if(transaction == null) {
                m_log.DebugFormat("[GLOEBITMONEYMODULE] Looking for prior transaction for {0}", transactionIDStr);
                GloebitTransaction[] transactions = GloebitTransactionData.Instance.Get("TransactionID", transactionIDStr);

                switch(transactions.Length) {
                case 1:
                    transaction = transactions[0];
                    m_log.DebugFormat("[GLOEBITMONEYMODULE] FOUND TRANSACTION! {0} {1} {2}", transaction.TransactionID, transaction.PayerID, transaction.PayeeID);
                    lock(s_transactionMap) {
                        s_transactionMap[transactionIDStr] = transaction;
                    }
                    return transaction;
                case 0:
                    m_log.DebugFormat("[GLOEBITMONEYMODULE] Could not find transaction matching tID:{0}", transactionIDStr);
                    return null;
                default:
                    throw new Exception(String.Format("[GLOEBITMONEYMODULE] Failed to find exactly one transaction for {0}", transactionIDStr));
                    return null;
                }
            }

            return transaction;
        }

        public Uri BuildEnactURI(Uri baseURI) {
            UriBuilder enact_uri = new UriBuilder(baseURI);
            enact_uri.Path = "gloebit/transaction";
            enact_uri.Query = String.Format("id={0}&state={1}", this.TransactionID, "enact");
            return enact_uri.Uri;
        }
        public Uri BuildConsumeURI(Uri baseURI) {
            UriBuilder consume_uri = new UriBuilder(baseURI);
            consume_uri.Path = "gloebit/transaction";
            consume_uri.Query = String.Format("id={0}&state={1}", this.TransactionID, "consume");
            return consume_uri.Uri;
        }
        public Uri BuildCancelURI(Uri baseURI) {
            UriBuilder cancel_uri = new UriBuilder(baseURI);
            cancel_uri.Path = "gloebit/transaction";
            cancel_uri.Query = String.Format("id={0}&state={1}", this.TransactionID, "cancel");
            return cancel_uri.Uri;
        }

        /**************************************************/
        /******* ASSET STATE MACHINE **********************/
        /**************************************************/

        public static bool ProcessStateRequest(string transactionIDstr, string stateRequested, IAssetCallback assetCallbacks, out string returnMsg) {
            bool result = false;

            // Retrieve asset
            GloebitTransaction myTxn = GloebitTransaction.Get(UUID.Parse(transactionIDstr));

            // If no matching transaction, return false
            // TODO: is this what we want to return?
            if (myTxn == null) {
                returnMsg = "No matching transaction found.";
                return false;
            }

            // Attempt to avoid race conditions (not sure if even possible)
            bool alreadyProcessing = false;
            lock(s_pendingTransactionMap) {
                alreadyProcessing = s_pendingTransactionMap.ContainsKey(transactionIDstr);
                if (!alreadyProcessing) {
                    // add to race condition protection
                    s_pendingTransactionMap[transactionIDstr] = myTxn;
                }
            }
            if (alreadyProcessing) {
                returnMsg = "pending";  // DO NOT CHANGE --- this message needs to be returned to Gloebit to know it is a retryable error
                return false;
            }

            // Call proper state processor
            switch (stateRequested) {
            case "enact":
                result = myTxn.enactHold(assetCallbacks, out returnMsg);
                break;
            case "consume":
                result = myTxn.consumeHold(assetCallbacks, out returnMsg);
                if (result) {
                    lock(s_transactionMap) {
                        s_transactionMap.Remove(transactionIDstr);
                    }
                }
                break;
            case "cancel":
                result = myTxn.cancelHold(assetCallbacks, out returnMsg);
                if (result) {
                    lock(s_transactionMap) {
                        s_transactionMap.Remove(transactionIDstr);
                    }
                }
                break;
            default:
                // no recognized state request
                returnMsg = "Unrecognized state request";
                result = false;
                break;
            }

            // remove from race condition protection
            lock(s_pendingTransactionMap) {
                s_pendingTransactionMap.Remove(transactionIDstr);
            }
            return result;
        }

        private bool enactHold(IAssetCallback assetCallbacks, out string returnMsg) {
            if (this.canceled) {
                // getting a delayed enact sent before cancel.  return false.
                returnMsg = "Enact: already canceled";
                return false;
            }
            if (this.consumed) {
                // getting a delayed enact sent before consume.  return true.
                returnMsg = "Enact: already consumed";
                return true;
            }
            if (this.enacted) {
                // already enacted. return true.
                returnMsg = "Enact: already enacted";
                return true;
            }
            // First reception of enact for asset.  Do specific enact functionality
            this.enacted = assetCallbacks.processAssetEnactHold(this, out returnMsg); // Do I need to grab the money module for this?

            // TODO: remove this after testing.
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitTransaction.enactHold: {0}", this.enacted);
            if (this.enacted) {
                m_log.InfoFormat("TransactionID: {0}", this.TransactionID);
                m_log.DebugFormat("PayerID: {0}", this.PayerID);
                m_log.DebugFormat("PayeeID: {0}", this.PayeeID);
                m_log.DebugFormat("PartID: {0}", this.PartID);
                m_log.DebugFormat("PartName: {0}", this.PartName);
                m_log.DebugFormat("CategoryID: {0}", this.CategoryID);
                m_log.DebugFormat("SaleType: {0}", this.SaleType);
                m_log.DebugFormat("Amount: {0}", this.Amount);
                m_log.DebugFormat("PayerEndingBalance: {0}", this.PayerEndingBalance);
                m_log.DebugFormat("enacted: {0}", this.enacted);
                m_log.DebugFormat("consumed: {0}", this.consumed);
                m_log.DebugFormat("canceled: {0}", this.canceled);
                m_log.DebugFormat("cTime: {0}", this.cTime);
                m_log.DebugFormat("enactedTime: {0}", this.enactedTime);
                m_log.DebugFormat("finishedTime: {0}", this.finishedTime);

                // TODO: Should we store and update the time even if it fails to track time enact attempted/failed?
                this.enactedTime = DateTime.UtcNow;
                GloebitTransactionData.Instance.Store(this);
            }
            return this.enacted;
        }

        private bool consumeHold(IAssetCallback assetCallbacks, out string returnMsg) {
            if (this.canceled) {
                // Should never get a delayed consume after a cancel.  return false.
                returnMsg = "Consume: already canceled";
                return false;
            }
            if (!this.enacted) {
                // Should never get a consume before we've enacted.  return false.
                returnMsg = "Consume: Not yet enacted";
                return false;
            }
            if (this.consumed) {
                // already consumed. return true.
                returnMsg = "Cosume: Already consumed";
                return true;
            }
            // First reception of consume for asset.  Do specific consume functionality
            this.consumed = assetCallbacks.processAssetConsumeHold(this, out returnMsg); // Do I need to grab the money module for this?
            if (this.consumed) {
                this.finishedTime = DateTime.UtcNow;
                GloebitTransactionData.Instance.Store(this);
            }
            return this.consumed;
        }

        private bool cancelHold(IAssetCallback assetCallbacks, out string returnMsg) {
            if (this.consumed) {
                // Should never get a delayed cancel after a consume.  return false.
                returnMsg = "Cancel: already consumed";
                return false;
            }
            if (!this.enacted) {
                // Hasn't enacted.  No work to undo.  return true.
                returnMsg = "Cancel: not yet enacted";
                // don't return here.  Still want to process cancel which will need to assess if enacted.
                //return true;
            }
            if (this.canceled) {
                // already canceled. return true.
                returnMsg = "Cancel: already canceled";
                return true;
            }
            // First reception of cancel for asset.  Do specific cancel functionality
            this.canceled = assetCallbacks.processAssetCancelHold(this, out returnMsg); // Do I need to grab the money module for this?
            if (this.canceled) {
                this.finishedTime = DateTime.UtcNow;
                GloebitTransactionData.Instance.Store(this);
            }
            return this.canceled;
        }
    }
}