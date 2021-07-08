﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using CompatApiClient.Compression;
using DSharpPlus.Entities;

namespace CompatBot.Utils
{
    public static class DiscordMessageExtensions
    {
        public static async Task<DiscordMessage> UpdateOrCreateMessageAsync(this DiscordMessage? botMsg, DiscordChannel channel, DiscordMessageBuilder messageBuilder)
        {
            Exception? lastException = null;
            for (var i = 0; i<3; i++)
                try
                {
                    Task<DiscordMessage> task;
                    if (botMsg is null)
                        task = channel.SendMessageAsync(messageBuilder);
                    else
                    {
                        if (messageBuilder.ReplyId is not null)
                        {
                            #warning Ugly hack, needs property reset method in the builder
                            var property = messageBuilder.GetType().GetProperty(nameof(messageBuilder.ReplyId));
                            property?.SetValue(messageBuilder, null, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty, null, null, null);
                        }
                        task = botMsg.ModifyAsync(messageBuilder);
                    }
                    var newMsg = await task.ConfigureAwait(false);
                    #warning Ugly hack, needs proper fix in upstream, but they are not enthused to do so
                    if (newMsg.Channel is null)
                    {
                        Config.Log.Warn("new message in DM from the bot still has no channel");
                        //newMsg.Channel = channel;
                        var property = newMsg.GetType().GetProperty(nameof(newMsg.Channel));
                        property?.SetValue(newMsg, channel, BindingFlags.NonPublic | BindingFlags.Instance, null, null, null);
                    }
                    return newMsg;
                }
                catch (Exception e)
                {
                    lastException = e;
                    if (i == 2 || e is NullReferenceException)
                    {
                        Config.Log.Error(e);
                        if (botMsg is not null)
                            try { await botMsg.DeleteAsync().ConfigureAwait(false); } catch { }
                        return await channel.SendMessageAsync(messageBuilder).ConfigureAwait(false);
                    }
                    Task.Delay(100).ConfigureAwait(false).GetAwaiter().GetResult();
                }
            throw lastException ?? new InvalidOperationException("Something gone horribly wrong");
        }

        public static Task<DiscordMessage> UpdateOrCreateMessageAsync(this DiscordMessage? botMsg, DiscordChannel channel, string? content = null, DiscordEmbed? embed = null, DiscordMessage? refMsg = null)
        {
            var msgBuilder = new DiscordMessageBuilder();
            if (content is {Length: >0})
                msgBuilder.WithContent(content);
            if (embed is not null)
                msgBuilder.WithEmbed(embed);
            if (refMsg is not null)
                msgBuilder.WithReply(refMsg.Id);
            return botMsg.UpdateOrCreateMessageAsync(channel, msgBuilder);
        }

        public static async Task<(Dictionary<string, Stream>? attachmentContent, List<string>? failedFilenames)> DownloadAttachmentsAsync(this DiscordMessage msg)
        {
            if (msg.Attachments.Count == 0)
                return (null, null);

            var attachmentContent = new Dictionary<string, Stream>(msg.Attachments.Count);
            var attachmentFilenames = new List<string>();
            using var httpClient = HttpClientFactory.Create(new CompressionMessageHandler());
            foreach (var att in msg.Attachments)
            {
                if (att.FileSize > Config.AttachmentSizeLimit)
                {
                    attachmentFilenames.Add(att.FileName);
                    continue;
                }

                try
                {
                    await using var sourceStream = await httpClient.GetStreamAsync(att.Url).ConfigureAwait(false);
                    var fileStream = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 16384, FileOptions.Asynchronous | FileOptions.RandomAccess | FileOptions.DeleteOnClose);
                    await sourceStream.CopyToAsync(fileStream, 16384, Config.Cts.Token).ConfigureAwait(false);
                    fileStream.Seek(0, SeekOrigin.Begin);
                    attachmentContent[att.FileName] = fileStream;
                }
                catch (Exception ex)
                {
                    Config.Log.Warn(ex, $"Failed to download attachment {att.FileName} from deleted message {msg.JumpLink}");
                    attachmentFilenames.Add(att.FileName);
                }
            }
            return (attachmentContent: attachmentContent, failedFilenames: attachmentFilenames);
        }
    }
}
