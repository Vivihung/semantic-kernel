﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.AI;
using Microsoft.SemanticKernel.Memory;
using Microsoft.SemanticKernel.Orchestration;
using SemanticKernel.Service.Config;
using SemanticKernel.Service.Model;
using SemanticKernel.Service.Skills;
using SemanticKernel.Service.Storage;

namespace SemanticKernel.Service.Controllers;

[ApiController]
public class SemanticKernelController : ControllerBase
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SemanticKernelController> _logger;

    public SemanticKernelController(IServiceProvider serviceProvider, IConfiguration configuration, ILogger<SemanticKernelController> logger)
    {
        this._serviceProvider = serviceProvider;
        this._configuration = configuration;
        this._logger = logger;
    }

    /// <summary>
    /// Invoke a Semantic Kernel function on the server.
    /// </summary>
    /// <remarks>
    /// We create and use a new kernel for each request.
    /// We feed the kernel the ask received via POST from the client
    /// and attempt to invoke the function with the given name.
    /// </remarks>
    /// <param name="kernel">Semantic kernel obtained through dependency injection</param>
    /// <param name="chatRepository">Storage repository to store chat sessions</param>
    /// <param name="chatMessageRepository">Storage repository to store chat messages</param>
    /// <param name="ask">Prompt along with its parameters</param>
    /// <param name="skillName">Skill in which function to invoke resides</param>
    /// <param name="functionName">Name of function to invoke</param>
    /// <returns>Results consisting of text generated by invoked function along with the variable in the SK that generated it</returns>
    [Route("skills/{skillName}/functions/{functionName}/invoke")]
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AskResult>> InvokeFunctionAsync(
        [FromServices] Kernel kernel,
        [FromServices] ChatSessionRepository chatRepository,
        [FromServices] ChatMessageRepository chatMessageRepository,
        [FromBody] Ask ask,
        string skillName, string functionName)
    {
        this._logger.LogDebug("Received call to invoke {SkillName}/{FunctionName}", skillName, functionName);

        string semanticSkillsDirectory = this._configuration.GetSection(CopilotChatApiConstants.SemanticSkillsDirectoryConfigKey).Get<string>();
        if (!string.IsNullOrWhiteSpace(semanticSkillsDirectory))
        {
            kernel.RegisterSemanticSkills(semanticSkillsDirectory, this._logger);
        }

        kernel.RegisterNativeSkills(chatRepository, chatMessageRepository, this._logger);

        ISKFunction? function = null;
        try
        {
            function = kernel.Skills.GetFunction(skillName, functionName);
        }
        catch (KernelException)
        {
            return this.NotFound($"Failed to find {skillName}/{functionName} on server");
        }

        // Put ask's variables in the context we will use
        var contextVariables = new ContextVariables(ask.Input);
        foreach (var input in ask.Variables)
        {
            contextVariables.Set(input.Key, input.Value);
        }

        // Run function
        SKContext result = await kernel.RunAsync(contextVariables, function!);
        if (result.ErrorOccurred)
        {
            // TODO latest NuGets don't have the Detail property on AIException
            //if (result.LastException is AIException aiException && aiException.Detail is not null)
            //{
            //    return this.BadRequest(string.Concat(aiException.Message, " - Detail: " + aiException.Detail));
            //}

            if (result.LastException is AIException aiException)
            {
                return this.BadRequest(aiException.Message);
            }

            return this.BadRequest(result.LastErrorDescription);
        }

        return this.Ok(new AskResult { Value = result.Result, Variables = result.Variables.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)) });
    }

    [Route("bot/import")]
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ImportAsync(
        [FromServices] Kernel kernel,
        [FromServices] ChatSessionRepository chatRepository,
        [FromServices] ChatMessageRepository chatMessageRepository,
        [FromQuery] string userId,
        [FromBody] Bot bot)
    {
        // TODO: We should get userId from server context instead of from request for privacy/security reasons when support multipe users.

        this._logger.LogDebug("Received call to import a bot");

        /*if (!this.IsBotCompatible(bot.Schema, bot.Configurations))
        {
            return new UnsupportedMediaTypeResult();
        }*/

        // TODO: Add real chat title, user object id and user name.
        string chatTitle = $"{bot.ChatTitle} - Clone";

        string chatId = string.Empty;

        // Import chat history into CosmosDB and embeddings into SK memory.
        try
        {
            // 1. Create a new chat and get the chat id.

            var newChat = new ChatSession(userId, chatTitle);
            await chatRepository.CreateAsync(newChat);
            chatId = newChat.Id;

            string oldChatId = bot.ChatHistory.First().ChatId;

            // 2. Update the app's chat storage.
            foreach (var message in bot.ChatHistory)
            {
                var chatMessage = new ChatMessage(message.UserId, message.UserName, chatId, message.Content, ChatMessage.AuthorRoles.Participant);
                chatMessage.Timestamp = message.Timestamp;
                // TODO: should we use UpsertItemAsync?
                await chatMessageRepository.CreateAsync(chatMessage);
            }

            // 3. Update SK memory.
            foreach (var collection in bot.Embeddings)
            {
                foreach (var record in collection.Value)
                {
                    var newCollectionKey = collection.Key.Replace(oldChatId, chatId, StringComparison.OrdinalIgnoreCase);
                    await kernel.Memory.UpsertAsync(newCollectionKey, record.Metadata.Text, record.Metadata.Id, record.Embedding);
                }
            }
        }
        catch
        {
            // TODO: Revert changes if any of the actions failed
            throw;
        }

        return this.Accepted();
    }

    [Route("bot/export")]
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<string>> ExportAsync(
        [FromServices] Kernel kernel,
        [FromServices] ChatSessionRepository chatRepository,
        [FromServices] ChatMessageRepository chatMessageRepository,
        [FromBody] Ask ask)
    {
        this._logger.LogDebug("Received call to export a bot");
        var memory = await this.GetAllMemoriesAsync(kernel, chatRepository, chatMessageRepository, ask);

        return JsonSerializer.Serialize(memory);
    }

    private bool IsBotCompatible(BotSchemaConfig externalBotSchema, BotConfiguration externalBotConfiguration)
    {
        var embeddingAIServiceConfig = this._configuration.GetSection("Embedding").Get<AIServiceConfig>();
        var botSchema = this._configuration.GetSection("BotFileSchema").Get<BotSchemaConfig>();

        if (embeddingAIServiceConfig != null && botSchema != null)
        {
            // The app can define what schema/version it supports before the community comes out with an open schema.
            return externalBotSchema.Name.Equals(
                botSchema.Name, StringComparison.OrdinalIgnoreCase)
                && externalBotSchema.Verson == botSchema.Verson
                && externalBotConfiguration.EmbeddingAIService.Equals(
                embeddingAIServiceConfig.AIService, StringComparison.OrdinalIgnoreCase)
                && externalBotConfiguration.EmbeddingDeploymentOrModelId.Equals(
                embeddingAIServiceConfig.DeploymentOrModelId, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            return false;
        }
    }

    /// <summary>
    /// Get the chat history and memory of a given chat.
    /// </summary>
    /// <param name="kernel">The semantic kernel object.</param>
    /// <param name="chatRepository">The chat session repository object.</param>
    /// <param name="chatMessageRepository">The chat message repository object.</param>
    /// <param name="ask">Prompt along with its parameters</param>
    private async Task<Bot> GetAllMemoriesAsync(
        Kernel kernel,
        ChatSessionRepository chatRepository,
        ChatMessageRepository chatMessageRepository,
        Ask ask)
    {
        kernel.RegisterNativeSkills(chatRepository, chatMessageRepository, this._logger);

        var bot = new Bot();

        // get the embedding configuration
        var embeddingAIServiceConfig = this._configuration.GetSection("Embedding").Get<AIServiceConfig>();
        bot.Configurations = new BotConfiguration
        {
            EmbeddingAIService = embeddingAIServiceConfig?.AIService ?? string.Empty,
            EmbeddingDeploymentOrModelId = embeddingAIServiceConfig?.DeploymentOrModelId ?? string.Empty
        };

        // get the chat title
        var chatId = ask.Input;
        ChatSession chat = await chatRepository.FindByIdAsync(chatId);
        bot.ChatTitle = chat.Title;

        // get the chat history
        var contextVariables = new ContextVariables(chatId);
        foreach (var input in ask.Variables)
        {
            contextVariables.Set(input.Key, input.Value);
        }
        var messages = await this.GetAllChatMessagesAsync(kernel, contextVariables);

        if (messages?.Value != null)
        {
            bot.ChatHistory = JsonSerializer.Deserialize<List<ChatMessage>>(messages.Value);
        }

        // get the memory
        var allCollections = await kernel.Memory.GetCollectionsAsync();
        List<MemoryQueryResult> allChatMessageMemories = new List<MemoryQueryResult>();

        foreach (var collection in allCollections)
        {
            var results = await kernel.Memory.SearchAsync(
                collection,
                "abc", // dummy query since we don't care about relevance. An empty string will cause exception.
                limit: 999999999, // hacky way to get as much as record as a workaround. TODO: Call GetAll() when it's in the SK memory storage API.
                minRelevanceScore: -1, // no relevance required since the collection only has one entry
                withEmbeddings: true,
                cancel: default
            ).ToListAsync();
            allChatMessageMemories.AddRange(results);

            bot.Embeddings.Add(new KeyValuePair<string, List<MemoryQueryResult>>(collection, allChatMessageMemories));
        }

        return bot;
    }

    /// <summary>
    /// Get chat messages from the ChatHistorySkill
    /// </summary>
    /// <param name="kernel">The semantic kernel object.</param>
    /// <param name="variables">The context variables.</param>
    /// <returns>The result of the ask.</returns>
    private async Task<AskResult> GetAllChatMessagesAsync(Kernel kernel, ContextVariables variables)
    {
        ISKFunction function = kernel.Skills.GetFunction("ChatHistorySkill", "GetAllChatMessages");

        // Invoke the GetAllChatMessages function of ChatHistorySkill.
        SKContext result = await kernel.RunAsync(variables, function!);
        return new AskResult { Value = result.Result, Variables = result.Variables.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)) };
    }
}
