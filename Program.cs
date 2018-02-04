﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Signer;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Newtonsoft.Json;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.SystemConsole.Themes;

namespace wallet
{
    class Program
    {
        static void Main(string[] args)
        {
            var minLevel = LogEventLevel.Information;
            if (args.Contains("--verbose")) {
                minLevel = LogEventLevel.Verbose;
            }

            if (!Directory.Exists("logs")) {
                Directory.CreateDirectory("logs");
            }
            
            ILogger log = null;
            try {
                
                log = new LoggerConfiguration()
                    .WriteTo.Console(
                        theme: AnsiConsoleTheme.Code,
                        outputTemplate: "{Timestamp:HH:mm} [{Level}] ({ThreadId}) ({SourceContext})  {Message}{NewLine}{Exception}",
                        restrictedToMinimumLevel: minLevel)
                    .Enrich.WithThreadId()
                    .MinimumLevel.Verbose()
                    .Enrich.FromLogContext()
                    .WriteTo.File(
                        new CompactJsonFormatter(),
                        Path.Combine("logs/log.txt"),
                        restrictedToMinimumLevel: minLevel,
                        rollOnFileSizeLimit: true,
                        rollingInterval: Serilog.RollingInterval.Hour)

                    .CreateLogger().ForContext<Program>();

                Task.WaitAll(MainAsync(args, log));                

            } catch (Exception e) {
                Console.WriteLine("Got an exception");
                log?.Error(e, "Unhandled global exception");
            } finally {
                Log.CloseAndFlush();
            }
        }

        static async Task MainAsync(string[] args, ILogger log) {
            var wallet = await Wallet.LoadOrCreate("wallet.json");

            bool shouldQuit = false;
            while (!shouldQuit) {
                shouldQuit = await MainMenu(wallet, log);
            }
        }

        static async Task<bool> MainMenu(Wallet wallet, ILogger log) {
            
            const string syncChoice = "Synchronize";
            const string listTxChoice = "List transactions";
            const string sendTxChoice = "Send transaction";
            const string quitChoice = "Quit";

            var choices = new [] { syncChoice, sendTxChoice, listTxChoice, quitChoice };

            var choice = await Menu(async () => {
                Console.WriteLine();
                Console.WriteLine("=============================================");
                Console.WriteLine();
                Console.WriteLine("Address: {0}", wallet.Address);
                Console.WriteLine("Balance: {0} ETH (at block {1})", (await wallet.FetchBalance()).WeiToEth(), wallet.LastProcessedBlock.Number);
                
                Console.WriteLine();
                Console.WriteLine("\tBalance and transactions were last updated at {0}", 
                    wallet.LastProcessedBlock.Timestamp.LocalDateTime);
                Console.WriteLine();

                Console.WriteLine();
            }, choices);
            
            if (choice == syncChoice) {
                await SyncNodes(wallet, log);
            }

            if (choice == sendTxChoice) {
                await SendTransaction(wallet, log);
            }

            if (choice == listTxChoice) {
                ListLocallyKnownTransactions(wallet, log);
            }

            if (choice == quitChoice) {
                return true;
            }

            return false;
        }

        static async Task<string> Menu(Action header, string[] choices) {
            return await Menu(() => Task.Run(header), choices);
        }

        static async Task<string> Menu(Func<Task> header, string[] choices) {
            await header();

            for (int ix = 0; ix < choices.Length; ++ix) {
                Console.WriteLine("{0}) {1}", ix, choices[ix]);
            }
            Console.WriteLine();
            
            int choice = -1;
            
            do {
                var key = Console.ReadKey(true);
                if (char.IsDigit(key.KeyChar)) {
                    choice = int.Parse(key.KeyChar.ToString());
                }
            } while (choice >= choices.Length || choice < 0);
            
            return choices[choice];
        }

        static async Task SendTransaction(Wallet wallet, ILogger log) {
            
            
            string recipient = ConsoleEx.PromptForAddress("Recipient (hex notation)");
            decimal amountInEth = ConsoleEx.PromptForDecimal("Amount (in ETH)");
            
            BigInteger gasPriceInGwei = new BigInteger(40);
            gasPriceInGwei = ConsoleEx.PromptForBigInteger(
                string.Format("Gas price (in Gwei) [{0}]", gasPriceInGwei),
                gasPriceInGwei);

            BigInteger gasAllowance = new BigInteger(21000);

            BigInteger? nonceOverride = ConsoleEx.PromptForOptionalBigInteger(
                "Nonce override (empty == automatic) []", 
                null);
            
            var maxFeeInWei = gasAllowance * gasPriceInGwei;
            var maxFeeInEth = maxFeeInWei.WeiToEth();

            var sendChoice = "Send";
            var choice = await Menu(() => {
                Console.WriteLine();
                Console.WriteLine("Do you want to send this transaction?");
                Console.WriteLine("\tFrom: {0}", wallet.Address);
                Console.WriteLine("\tTo: {0}", recipient);
                Console.WriteLine("\tAmount: {0} ETH", amountInEth);
                Console.WriteLine("\tMax fee: {0} ETH", maxFeeInEth);
                Console.WriteLine("\tNonce: {0}", nonceOverride?.ToString() ?? "<automatic>");
                Console.WriteLine();
            }, new [] { sendChoice, "Cancel" });

            if (choice == sendChoice) {
                Console.WriteLine("Wait a second, I'll check if we can afford this transaction... ");
                Console.WriteLine();

                var amountInWei = amountInEth.EthToWei();
                var maxExpenditureInWei = maxFeeInWei + amountInWei;
                var knownBalanceInWei = await wallet.FetchBalance();
                
                if (maxExpenditureInWei > knownBalanceInWei) {
                    Console.WriteLine("\tSORRY! I can not send this transaction:");
                    Console.WriteLine();
                    Console.WriteLine("\tThis transaction may require more ETH than you have.");
                    Console.WriteLine("\tIf you think this is an error, try re-synchronizing the wallet.");
                    return;
                }

                Console.Write("Sending... ");
                try {
                    var hash = wallet.SendTransaction(
                        recipient, 
                        amountInWei, 
                        gasPriceInGwei.GweiToWei(),
                        gasAllowance,
                        nonceOverride
                    );
                    
                    Console.WriteLine("done.");
                    Console.WriteLine();
                    Console.WriteLine("You can see your transaction status at:");
                    Console.WriteLine("\thttps://rinkeby.etherscan.io/tx/{0}", hash);
                    Console.WriteLine();
                } catch (Exception e) {
                    Console.WriteLine("FAILED: {0}", e.Message);
                    log.Error(e, "Failed to send transaction");
                }
            } else {
                Console.WriteLine("Transaction cancelled");
            }

        }
        static void ListLocallyKnownTransactions(Wallet wallet, ILogger log) {
            Console.WriteLine("Transaction history: ");
            var sortedTx = wallet.KnownTransactions.OrderByDescending(t => t.BlockNumber);

            foreach (var tx in sortedTx) {
                Console.WriteLine();

                var isIncomingTransaction = string.Equals(tx.To, wallet.Address, StringComparison.OrdinalIgnoreCase);

                var sb = new StringBuilder();
                if (isIncomingTransaction) {
                    sb.AppendLine("RECEIVED: ");
                    sb.AppendFormat("\tFrom: {0}", tx.From); sb.AppendLine();
                } else {
                    sb.AppendLine("SENT: ");
                    sb.AppendFormat("\tTo: {0}", tx.To); sb.AppendLine();
                }

                sb.AppendFormat("\tHash: {0}", tx.TxHash); sb.AppendLine();
                sb.AppendFormat("\tTime: {0}", tx.BlockCreatedAt.LocalDateTime); sb.AppendLine();
                sb.AppendFormat("\tAmount (without fee): {0} ETH", tx.AmountInWei.WeiToEth()); sb.AppendLine();

                if (isIncomingTransaction) {
                    // Not showing the fee for incoming transactions, because we did not pay it
                } else {
                    sb.AppendFormat("\tPaid transaction fee: {0} ETH", tx.FeeInEth);
                    sb.AppendLine();
                }
                
                sb.AppendLine();
                sb.AppendLine("\tSee this transaction on etherscan.io:");
                sb.AppendFormat("\thttps://rinkeby.etherscan.io/tx/{0}", tx.TxHash); sb.AppendLine();
                

                Console.WriteLine("{0}", sb.ToString());
                Console.WriteLine();
            }
        }

        static async Task SyncNodes(Wallet wallet, ILogger log) {
            
            Console.WriteLine("Synchronizing wallet with the network... ");

            var maxKnownBlockNumber = (await wallet.GetMaxKnownRemoteBlock());

            log.Debug("Max known block {number}", maxKnownBlockNumber);
            log.Debug("Latest processed block {number}", wallet.LastProcessedBlock);
            
            var catchupDelta = maxKnownBlockNumber - wallet.LastProcessedBlock.Number;
            var catchupMin = wallet.LastProcessedBlock.Number;

            log.Debug("Catching up by {delta} blocks", catchupDelta);
            
            for (   var currentBlockNumber = wallet.LastProcessedBlock.Number; 
                    currentBlockNumber <= maxKnownBlockNumber; 
                    ++currentBlockNumber) 
            {
                var percentage = (double)(currentBlockNumber - catchupMin) / (double)catchupDelta;

                log.Debug("Processing block {number} / {maxKnown} ({percentage:0.00}%)", 
                    currentBlockNumber, 
                    maxKnownBlockNumber,
                    percentage * 100.0);

                await wallet.LoadAndSaveTransactionsForBlock(currentBlockNumber);
            }
        }
    }

    
}
