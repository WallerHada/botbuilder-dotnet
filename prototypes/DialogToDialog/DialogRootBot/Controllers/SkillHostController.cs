﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.IdentityModel.Tokens.Jwt;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Builder.Integration.AspNet.Core.Skills;

namespace DialogRootBot.Controllers
{
    [ApiController]
    [Route("/v3/conversations/{*path}")]
    public class SkillHostController : ControllerBase
    {
        private readonly BotFrameworkHttpAdapter _adapter;
        private readonly IBot _bot;
        private readonly BotFrameworkSkillRequestHandler _skillRequestHandler;

        public SkillHostController(BotFrameworkSkillRequestHandler skillRequestHandler, BotFrameworkHttpAdapter adapter, IBot bot)
        {
            // adapter to use for calling back to channel
            _adapter = adapter;
            _bot = bot;
            _skillRequestHandler = skillRequestHandler;
        }

        [HttpPost]
        [HttpGet]
        [HttpPut]
        [HttpDelete]
        public async Task ProcessAsync()
        {
            // Entering parent from skill
            var authToken = Request.Headers["Authorization"].ToString();

            //DebugTokenClaims();

            // Delegate the processing of the HTTP POST to the adapter.
            // The adapter will invoke the bot.
            try
            {
                await _skillRequestHandler.ProcessAsync(Request, Response, _adapter, _bot);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                throw;
            }
        }

        /// <summary>
        /// Helper to debug claims returned by skill.
        /// </summary>
        private void DebugTokenClaims()
        {
            var authToken = Request.Headers["Authorization"];
            var jwtToken = new JwtSecurityToken(authToken.ToString().Split(' ')[1]);
            foreach (var claim in jwtToken.Claims)
            {
                Console.WriteLine($"Claim: {claim.Type} Value: {claim.Value}");
            }
        }
    }
}
