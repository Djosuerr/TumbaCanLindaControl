using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Timers;
using Tumba.CanLindaControl.DataConnectors.Linda;
using Tumba.CanLindaControl.Model.Linda.Requests;
using Tumba.CanLindaControl.Model.Linda.Responses;

namespace Tumba.CanLindaControl.Services
{
    public class CoinControlService : IDisposable
    {
        public const string COMPATIBLE_WALLET_VERSIONS = "v1.0.1.3-g";
        public const int DEFAULT_CONFIRMATION_COUNT_REQUIRED_FOR_COIN_CONTROL = 10;
        public const int DEFAULT_FREQUENCY = 60000; // 1 minute

        private LindaDataConnector m_dataConnector;
        private string m_accountToCoinControl;
        private string m_walletPassphrase;
        private System.Timers.Timer m_timer;
        private object m_coinControlLock = new object();

        public int FrequencyInMilliSeconds { get; private set; }
        public ConsoleMessageHandlingService MessageService { get; private set; }

        public CoinControlService(ConsoleMessageHandlingService messageService)
        {
            MessageService = messageService;
        }

        private bool CheckStakingInfo()
        {
            string errorMessage;

            StakingInfoRequest stakingInfoRequest = new StakingInfoRequest();
            StakingInfoResponse stakingInfoResponse;
            if (!m_dataConnector.TryPost<StakingInfoResponse>(
                stakingInfoRequest,
                out stakingInfoResponse,
                out errorMessage))
            {
                MessageService.PostError(stakingInfoRequest, errorMessage);
                return false;
            }

            if (!stakingInfoResponse.Enabled)
            {
                MessageService.Warning(string.Format("Staking is disabled!"));
            }

            MessageService.Info(string.Format("Staking: {0}.",
                (stakingInfoResponse.Staking ? "Yes" : "No")));

            if (stakingInfoResponse.Staking)
            {
                TimeSpan timeSpan = TimeSpan.FromSeconds(stakingInfoResponse.ExpectedTimeInSeconds);
                MessageService.Info(string.Format("Expected time to earn reward: {0} days {1} hours.", timeSpan.Days, timeSpan.Hours));
            }

            if (!string.IsNullOrEmpty(stakingInfoResponse.Errors))
            {
                MessageService.Error(string.Format("Staking errors found: {0}", stakingInfoResponse.Errors));
            }

            return  true;
        }

        private bool CheckWalletCompaitibility()
        {
            InfoResponse info;
            InfoRequest requestForInfo = new InfoRequest();
            string errorMessage;

            MessageService.Info("Connecting and reading Linda wallet info...");

            if (!m_dataConnector.TryPost<InfoResponse>(requestForInfo, out info, out errorMessage))
            {
                MessageService.Fail(errorMessage);
                return false;
            }

            MessageService.Info("Linda wallet info retrieved!");
            MessageService.Info("Checking for wallet compatibility...");

            if (!COMPATIBLE_WALLET_VERSIONS.Contains(info.Version.ToLower()))
            {
                MessageService.Fail(string.Format(
                    "Linda wallet version: '{0}' is not compatible!",
                    info.Version));

                MessageService.Fail(string.Format(
                    "See compatible versions: {0}",
                    COMPATIBLE_WALLET_VERSIONS));
                
                return false;
            }

            MessageService.Info("Wallet compatibility check complete!");
            return true;
        }

        private void DoCoinControl()
        {
            MessageService.Break();
            MessageService.Info(string.Format("Account: {0}.", m_accountToCoinControl));

            if (!TryUnlockWallet(FrequencyInMilliSeconds * 3, true))
            {
                return;
            }

            if (!CheckStakingInfo())
            {
                return;
            }

            List<UnspentResponse> unspentInNeedOfCoinControl = GetUnspentInNeedOfCoinControl();
            if (unspentInNeedOfCoinControl.Count < 2)
            {
                return;
            }

            MessageService.Info("Coin control needed.  Starting...");

            decimal amount = GetAmount(unspentInNeedOfCoinControl);
            decimal fee = GetFee();
            if (fee < 0)
            {
                return;
            }

            decimal amountAfterFee = amount - fee;
            MessageService.Info(string.Format("Amount After Fee: {0} LINDA.", amountAfterFee));

            if (!TryUnlockWallet(5, false))
            {
                return;
            }

            if (!TrySendFrom(
                m_accountToCoinControl,
                unspentInNeedOfCoinControl[0].Address,
                amountAfterFee))
            {
                return;
            }

            TryUnlockWallet(FrequencyInMilliSeconds * 3, true);

            MessageService.Info("Wallet unlocked for staking.");
            MessageService.Info("Coin control complete!");
        }

        public void Dispose()
        {
            lock(m_coinControlLock)
            {
                if (m_timer != null)
                {
                    m_timer.Dispose();
                    m_timer = null;
                }
            }
        }

        private decimal GetAmount(List<UnspentResponse> unspentInNeedOfCoinControl)
        {
            decimal amount = 0;
            string address = unspentInNeedOfCoinControl[0].Address;
            foreach (UnspentResponse unspent in unspentInNeedOfCoinControl)
            {
                amount += unspent.Amount;
            }

            MessageService.Info(string.Format("Amount: {0} LINDA.", amount));

            return amount;
        }

        private decimal GetFee()
        {
            string errorMessage;
            InfoRequest requestForInfo = new InfoRequest();
            InfoResponse info;
            if (!m_dataConnector.TryPost<InfoResponse>(requestForInfo, out info, out errorMessage))
            {
                MessageService.PostError(requestForInfo, errorMessage);
                return -1;
            }
            
            MessageService.Info(string.Format("Fee: {0} LINDA.", info.Fee));

            return info.Fee;
        }

        private List<UnspentResponse> GetUnspentInNeedOfCoinControl()
        {
            string errorMessage;
            ListUnspentRequest unspentRequest = new ListUnspentRequest();
            List<UnspentResponse> unspentResponses;
            if (!m_dataConnector.TryPost<List<UnspentResponse>>(
                unspentRequest, 
                out unspentResponses, 
                out errorMessage))
            {
                MessageService.PostError(unspentRequest, errorMessage);
                return new List<UnspentResponse>();
            }

            List<UnspentResponse> unspentForAccount = new List<UnspentResponse>();
            foreach (UnspentResponse unspent in unspentResponses)
            {
                if (unspent.Account != null && 
                    unspent.Account.Equals(m_accountToCoinControl, StringComparison.InvariantCultureIgnoreCase))
                {
                    if (unspent.Confirmations < DEFAULT_CONFIRMATION_COUNT_REQUIRED_FOR_COIN_CONTROL)
                    {
                        MessageService.Info(string.Format(
                            "Waiting for more confirmations: {0}/{1} {2} LINDA {3}",
                            unspent.Confirmations,
                            DEFAULT_CONFIRMATION_COUNT_REQUIRED_FOR_COIN_CONTROL,
                            unspent.Amount,
                            unspent.TransactionId));

                        return unspentResponses;
                    }

                    unspentForAccount.Add(unspent);
                }
            }

            if (unspentForAccount.Count < 1)
            {
                MessageService.Info("No unspent transactions.");
            }
            else if (unspentForAccount.Count == 1)
            {
                MessageService.Info("Only one unspent transaction.");
            }

            return unspentForAccount;
        }

        public static void Run(string[] args)
        {
            using (ManualResetEvent wait = new ManualResetEvent(false))
            {
                ConsoleMessageHandlingService messageHandler = new ConsoleMessageHandlingService(() =>
                {
                    wait.Set();
                });

                using (CoinControlService service = new CoinControlService(messageHandler))
                {
                    string errorMessage;
                    if (!service.TryParseArgs(args, out errorMessage))
                    {
                        Console.WriteLine(errorMessage);
                        Environment.Exit(-2);
                    }
                    service.Start();

                    wait.WaitOne();
                }
            }
        }

        private void Start()
        {
            if (!CheckWalletCompaitibility())
            {
                return;
            }

            // Attempt 2 unlocks to work around a wallet bug where the first unlock for staking only seems to unlock the whole wallet.
            if (!TryUnlockWallet(FrequencyInMilliSeconds * 3, true || 
                !TryUnlockWallet(FrequencyInMilliSeconds * 3, true)))
            {
                return;
            }

            DoCoinControl();

            MessageService.Info(string.Format("Coin control set to run every {0} milliseconds.", FrequencyInMilliSeconds));
            m_timer.Start();
        }

        private bool TryParseArgs(string[] args, out string errorMessage)
        {
            if (args.Length < 5)
            {
                errorMessage = "Missing required parameters.";
                return false;
            }

            m_dataConnector = new LindaDataConnector(args[1].Trim(), args[2].Trim()); // user, password
            m_accountToCoinControl = args[3].Trim();
            m_walletPassphrase = args[4].Trim();
            FrequencyInMilliSeconds = DEFAULT_FREQUENCY;

            int tmpFrequency;
            if (args.Length >= 6 && Int32.TryParse(args[5], out tmpFrequency))
            {
                FrequencyInMilliSeconds = tmpFrequency;
            }

            m_timer = new System.Timers.Timer();
            m_timer.AutoReset = false;
            m_timer.Interval = FrequencyInMilliSeconds;
            m_timer.Elapsed += (sender, eventArgs) =>
            {
                lock(m_coinControlLock)
                {
                    try
                    {
                        DoCoinControl();
                        if (m_timer != null)
                        {
                            m_timer.Start();
                        }
                    }
                    catch (Exception exception)
                    {
                        MessageService.Fail(string.Format("Coin control failed!  See exception: {0}", exception));
                    }
                }
            };

            errorMessage = null;
            return true;
        }

        private bool TrySendFrom(string fromAccount, string toAddress, decimal amountAfterFee)
        {
            SendFromRequest sendRequest = new SendFromRequest()
            {
                FromAccount = m_accountToCoinControl,
                ToAddress = toAddress,
                AmountAfterFee = amountAfterFee
            };

            string errorMessage;
            string transactionId;
            if (!m_dataConnector.TryPost<string>(sendRequest, out transactionId, out errorMessage))
            {
                MessageService.PostError(sendRequest, errorMessage);
                return false;
            }

            MessageService.Info(string.Format("Coin control transaction sent: {0}.", transactionId));
            return true;
        }

        private bool TryUnlockWallet(int timeout, bool forStakingOnly)
        {
            string lockError, errorMessage;

            WalletPassphraseRequest unlockRequest = new WalletPassphraseRequest(m_walletPassphrase);
            unlockRequest.StakingOnly = forStakingOnly;
            unlockRequest.TimeoutInSeconds = timeout;
            if (!m_dataConnector.TryPost<string>(unlockRequest, out lockError, out errorMessage))
            {
                MessageService.Error("Failed to unlock wallet!  Is the passphrase correct?");
                MessageService.PostError(unlockRequest, errorMessage);
                return false;
            }

            if (!string.IsNullOrEmpty(lockError))
            {
                MessageService.Error(string.Format("Unlock request returned error: {0}", lockError));
                return false;
            }

            return true;
        }
    }
}