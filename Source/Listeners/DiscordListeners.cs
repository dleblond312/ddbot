﻿using DDBot.Configuration;
using DDBot.Models;
using DDBot.Services;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Discord.Audio;
using System.Windows.Forms.DataVisualization.Charting;
using NAudio.Wave;

namespace DDBot.Listeners
{
    public class DiscordListeners : IDiscordListeners
    {
        private const string HelpText =
@"**Available commands**
```
!sentiment - Your personal sentiment score
!analysis - The overall analysis for the channel
!memory - how many scores are stored for this channel
!joinvoice - joins the voice you are currently connected to
```";

        private readonly Config config;
        private readonly DiscordSocketClient discordClient;
        private readonly SentimentService sentimentService;
        private readonly SentimentHistoryService sentimentHistoryService;
        private readonly SentimentSummaryService sentimentSummaryService;
        private readonly ChartService chartService;
        private readonly SemaphoreSlim semaphore;
        private readonly VoiceToTextService voiceToTextService;
        private readonly Dictionary<ulong, SocketVoiceChannel> audioChannels;
        private readonly Dictionary<ulong, SemaphoreSlim> semaphores;
        private readonly Dictionary<ulong, Stream> streams;
        private readonly SemaphoreSlim initializedSemaphore;

        public DiscordListeners(Config config, DiscordSocketClient discordClient, SentimentService sentimentService, SentimentHistoryService sentimentHistoryService, SentimentSummaryService sentimentSummaryService, VoiceToTextService voiceToTextService, ChartService chartService)
        {
            this.config = config;
            this.discordClient = discordClient;
            this.sentimentService = sentimentService;
            this.sentimentHistoryService = sentimentHistoryService;
            this.sentimentSummaryService = sentimentSummaryService;
            this.chartService = chartService;
            this.semaphore = new SemaphoreSlim(1);

            this.voiceToTextService = voiceToTextService;
            this.audioChannels = new Dictionary<ulong, SocketVoiceChannel>();
            this.semaphores = new Dictionary<ulong, SemaphoreSlim>();
            this.streams = new Dictionary<ulong, Stream>();
            this.initializedSemaphore = new SemaphoreSlim(1);

        }

        public Task Log(LogMessage msg)
        {
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        }

        public async Task MessageReceived(SocketMessage message)
        {
            if(message.Author.IsBot)
            {
                return;
            }
            switch (message.Content)
            {
                case "!help":
                    await message.Channel.SendMessageAsync(HelpText);
                        break;
                case "!ping":
                    await message.Channel.SendMessageAsync("Pong!");
                    break;
                case "!sentiment":
                    var userSentiment = this.sentimentSummaryService.CalculateAverageUserSentiment(message.Channel.Id, message.Author.Id);
                    if (userSentiment >= 0.5)
                    {
                        await message.Channel.SendMessageAsync($"{message.Author.Username}, your current sentiment score is {userSentiment.ToString("0.00")}. Good job, budday!");
                    }
                    else if (userSentiment >= 0)
                    {
                        await message.Channel.SendMessageAsync($"{message.Author.Username}, your sentiment score is {userSentiment.ToString("0.00")} - better work on that.");
                    }
                    else
                    {
                        await message.Channel.SendMessageAsync($"{message.Author.Username}, no score found for you, friend.");
                    }
                    break;
                case "!analysis":
#pragma warning disable CS4014
                    Task.Factory.StartNew(async () =>
#pragma warning restore CS4014
                    {
                        var userDailySummary = this.sentimentSummaryService.GenerateChannelAnalysis(message.Channel.Id);
                        var userDailyLevel = userDailySummary.GroupBy(x => x.Key.Split('-')[0]);

                        List<DataPoint> list = new List<DataPoint>();
                        List<Tuple<string, double>> users = new List<Tuple<string, double>>();
                        int userCount = 1;

                        foreach (var userData in userDailyLevel)
                        {
                            int messageCount = 0;
                            double scoreAgg = 0;
                            double totalScore = 0;

                            foreach (var messageData in userData)
                            {
                                scoreAgg += messageData.Value.Score;
                                messageCount += messageData.Value.Count;
                            }

                            totalScore = (scoreAgg / messageCount) * 100;
                            users.Add(Tuple.Create(userData.Key, totalScore));
                            userCount++;
                        }

                        users.Sort((x, y) => y.Item2.CompareTo(x.Item2));

                        for (var i = 1; i <= users.Count(); i++)
                        {
                            var item = new DataPoint(i, users[i - 1].Item2)
                            {
                                AxisLabel = users[i - 1].Item1
                            };
                            list.Add(item);
                        }

                        chartService.GeneratePlot(list);
                        await message.Channel.SendFileAsync("a_mypic.png", $"Channel-wide \"Sentiment\" scores... {list[Math.Max(list.Count() - 1, 0)].AxisLabel} could use a hug.");
                    });
                    break;
                case "!memory":
                    await message.Channel.SendMessageAsync($"There are {sentimentHistoryService.GetMessages(message.Channel.Id)?.Count ?? 0} messages(s) stored for this channel.");
                    break;
                case "!joinvoice":
                    var author = message.Author as SocketGuildUser;
                    if(author?.VoiceChannel?.Id != null)
                    {
                        await Task.Factory.StartNew(async () =>
                        {
                            var audioClient = await author.VoiceChannel.ConnectAsync();
                        });
                    }
                    else
                    {
                        await message.Channel.SendMessageAsync($"You aren't in a voice channel, please connect first then re-issue command.");
                    }
                    break;
                default:
                    var blocked = this.sentimentHistoryService.CheckSpamBlock(message);
                    if (!blocked)
                    {
                        var result = await sentimentService.AnalyzeMessage(new List<SentimentMessage>() { new SentimentMessage(message) });
                        await this.sentimentHistoryService.StoreMessage(result);
                    }
                    return;
            }
        }

        public async Task Ready()
        {
            await initializedSemaphore.WaitAsync();
            try
            {
                var HasInitializedPath = "Data/HasInitialized.json";
                var initialized = new List<HasInitialized>();
                var guilds = this.discordClient.Guilds;
                var channels = guilds.SelectMany(x => x.Channels).Where(z => z as IMessageChannel != null).Select(y => y as IMessageChannel);
            
                // Remove anything already initialized
                if (File.Exists(HasInitializedPath))
                {
                    initialized = JsonConvert.DeserializeObject<List<HasInitialized>>(File.ReadAllText(HasInitializedPath));
                    channels = channels.Where(x => !initialized.Where(y => y.channelId == x.Id).Any());
                }

                // Iterate all uninitialized channels and bootstrap messages
                foreach (var channel in channels)
                {
                    // TODO: check access first, do not initialize channels with no permissions
                    var messages = channel.GetMessagesAsync(config.OnChannelJoinBootstrapMessageCount, CacheMode.AllowDownload);
                    try
                    {
                        await messages.ForEachAsync(async (messageSet) =>
                        {
                            if (messageSet.Count() > 0)
                            {
                                // Do not initialize bots
                                var sentimentScores = await this.sentimentService.AnalyzeMessage(messageSet.Where(z => !z.Author.IsBot).Select(x => new SentimentMessage(x)).ToList());
                                await this.sentimentHistoryService.StoreMessage(sentimentScores);
                            }
                        });
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Cannot initialize a channel: {e} ");
                    }

                    // Mark as initialized
                    initialized.Add(new HasInitialized()
                    {
                        channelId = channel.Id,
                        timestamp = DateTime.UtcNow
                    });
                }

                File.WriteAllText(HasInitializedPath, JsonConvert.SerializeObject(initialized));
            }
            finally
            {
                initializedSemaphore.Release();
            }
        }

        public async Task JoinedGuild(SocketGuild guild)
        {
            await this.Ready();
        }

        public async Task UserVoiceStateUpdated(SocketUser user, SocketVoiceState from, SocketVoiceState to)
        {
            if (user.Id == discordClient.CurrentUser.Id)
            {
                var guildUser = user as SocketGuildUser;
                if (to.VoiceChannel != null && guildUser != null)
                {
                    audioChannels[guildUser.Id] = to.VoiceChannel;

                    Console.WriteLine(user.GetType());
                    Console.WriteLine(to.GetType());
                    Console.WriteLine($"To ID: {to.VoiceSessionId}");
                    guildUser.Guild.AudioClient.StreamCreated += StreamCreated;

                    guildUser.Guild.AudioClient.SpeakingUpdated += SpeakingUpdated;
                }
                Console.WriteLine($"Bot voice state change: {user.Username}, to: {from}, from: {to}");
            }
        }

        private async Task SpeakingUpdated(ulong userId, bool isSpeaking)
            {
            SemaphoreSlim semaphore;
            var user = this.discordClient.GetUser(userId);

            if (!semaphores.TryGetValue(userId, out semaphore))
                {
                semaphores[userId] = new SemaphoreSlim(1);
                semaphore = semaphores[userId];
                }


            if(!isSpeaking)
                {
#pragma warning disable CS4014
                    Task.Factory.StartNew(async () =>
#pragma warning restore CS4014
                {
                    Stream stream;

                    if (!streams.TryGetValue(userId, out stream))
                    {
                        streams[userId] = new MemoryStream();
                        stream = streams[userId];
                    }

                    try
                    {
                        // Wait for the other thread to parse the whole set of frames
                        while(stream.Length != stream.Position)
                        {
                            await semaphore.WaitAsync();
                            Console.WriteLine($"Length:{stream.Length}, Position: {stream.Position}");
                            await Task.Delay(1000);
                        }

                        // Wait on the semaphore synchronization
                        await semaphore.WaitAsync();

                        if (stream.Length > 0)
                        {
                            // Console.WriteLine("Sent for processing");
                            var rate = 46000;
                            var text = await this.voiceToTextService.ProcessVoiceToText(stream, rate);
                            Console.WriteLine($"STT {user.Username}: {text}");

                            if(text.Length > 2)
                            {
                                await user.SendMessageAsync("VTT:" + text);
                            }

                            // Reset the stream
                            stream.SetLength(0);
                        }
                    }
                    finally
                    {
                    semaphore.Release();
                    }
                });
            }
        }
                    
        private async Task StreamCreated(ulong userId, AudioInStream audio)
        {
            //var channels = this.discordClient.Guilds.SelectMany(g => g.Channels);
            //var voiceChannels = channels.Where(x => x.Users.Where(z => z.Id == userId).Any()).Select(z => z as SocketVoiceChannel).Where(y => y != null);

#pragma warning disable CS4014
            Task.Factory.StartNew(async () =>
#pragma warning restore CS4014
            {
                SemaphoreSlim semaphore;
                Stream stream;

                if (!semaphores.TryGetValue(userId, out semaphore))
                {
                    semaphores[userId] = new SemaphoreSlim(1);
                    semaphore = semaphores[userId];
                }

                var user = this.discordClient.GetUser(userId);
                do
                {
                    try
                    {
                        // Wait for a frame to show up on the audio channel
                        if(audio.AvailableFrames > 0)
                        {
                            try
                            {
                                await semaphore.WaitAsync();
                                RTPFrame frame = await audio.ReadFrameAsync(new CancellationToken());
                                // Wait on the semaphore synchronization
                                if (!streams.TryGetValue(userId, out stream))
                                {
                                    streams[userId] = new MemoryStream();
                                    stream = streams[userId];
                                }

                                // Write the payload to the memory stream
                                stream.Write(frame.Payload, 0, frame.Payload.Length);
                                // Console.WriteLine($"Frame received for user {user.Username} - {stream.Length}");
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }
                        else
                        {
                            await Task.Delay(500);
                        }
                    }
                    catch (Exception e) { Console.WriteLine(e); }

                } while (audio.CanRead);
            });
        }

        private async Task AudioConnected()
        {
            Console.WriteLine("Audio connected");
        }
    }
}
