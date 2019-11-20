﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Builder.Skills;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Configuration;

namespace SimpleRootBot.Bots
{
    public class RootBot : ActivityHandler
    {
        private readonly IStatePropertyAccessor<string> _activeSkillProperty;
        private readonly string _botId;
        private readonly ISkillConversationIdFactory _conversationIdFactory;
        private readonly ConversationState _conversationState;
        private readonly BotFrameworkHttpClient _skillClient;
        private readonly SkillsConfiguration _skillsConfig;

        public RootBot(ConversationState conversationState, SkillsConfiguration skillsConfig, ISkillConversationIdFactory conversationIdFactory, BotFrameworkHttpClient skillClient, IConfiguration configuration)
        {
            _conversationIdFactory = conversationIdFactory;
            _botId = configuration.GetSection(MicrosoftAppCredentials.MicrosoftAppIdKey)?.Value;
            _skillClient = skillClient;
            _skillsConfig = skillsConfig;
            _conversationState = conversationState;
            _activeSkillProperty = conversationState.CreateProperty<string>("activeSkillProperty");
        }

        protected override async Task OnMessageActivityAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            // if there is an active skill
            var activeSkillId = await _activeSkillProperty.GetAsync(turnContext, () => null, cancellationToken);
            var skillConversationId = _conversationIdFactory.CreateSkillConversationId(turnContext.Activity.Conversation.Id, turnContext.Activity.ServiceUrl);

            if (activeSkillId != null)
            {
                // NOTE: Always SaveChanges() before calling a skill so that any activity generated by the skill will have access to current accurate state.
                await _conversationState.SaveChangesAsync(turnContext, force: true, cancellationToken: cancellationToken);

                // route activity to the skill
                await _skillClient.PostActivityAsync(_botId, _skillsConfig.Skills[activeSkillId].AppId, _skillsConfig.Skills[activeSkillId].SkillEndpoint, _skillsConfig.SkillHostEndpoint, skillConversationId, (Activity)turnContext.Activity, cancellationToken);
            }
            else
            {
                if (turnContext.Activity.Text.Contains("skill"))
                {
                    await turnContext.SendActivityAsync(MessageFactory.Text("Got it, connecting you to the skill..."), cancellationToken);

                    // save conversationReference for skill
                    await _activeSkillProperty.SetAsync(turnContext, "SkillBot", cancellationToken);

                    // NOTE: Always SaveChanges() before calling a skill so that any activity generated by the skill will have access to current accurate state.
                    await _conversationState.SaveChangesAsync(turnContext, force: true, cancellationToken: cancellationToken);

                    // route the activity to the skill
                    await _skillClient.PostActivityAsync(_botId, _skillsConfig.Skills["SkillBot"].AppId, _skillsConfig.Skills["SkillBot"].SkillEndpoint, _skillsConfig.SkillHostEndpoint, skillConversationId, (Activity)turnContext.Activity, cancellationToken);
                }
                else
                {
                    // just respond
                    await turnContext.SendActivityAsync(MessageFactory.Text("Me no nothin'. Say \"skill\" and I'll patch you through"), cancellationToken);
                }
            }
        }

        protected override async Task OnMembersAddedAsync(IList<ChannelAccount> membersAdded, ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
        {
            foreach (var member in membersAdded)
            {
                if (member.Id != turnContext.Activity.Recipient.Id)
                {
                    await turnContext.SendActivityAsync(MessageFactory.Text("Hello and welcome!"), cancellationToken);
                }
            }
        }

        protected override async Task OnUnrecognizedActivityTypeAsync(ITurnContext turnContext, CancellationToken cancellationToken)
        {
            if (turnContext.Activity.Type == ActivityTypes.EndOfConversation)
            {
                // forget skill invocation
                await _activeSkillProperty.DeleteAsync(turnContext, cancellationToken);
                await _conversationState.SaveChangesAsync(turnContext, force: true, cancellationToken: cancellationToken);

                // We are back
                await turnContext.SendActivityAsync(MessageFactory.Text("Back in the root bot. Say \"skill\" and I'll patch you through"), cancellationToken);
            }

            await base.OnUnrecognizedActivityTypeAsync(turnContext, cancellationToken);
        }
    }
}
