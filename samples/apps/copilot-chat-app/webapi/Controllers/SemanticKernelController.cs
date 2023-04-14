﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.AI;
using Microsoft.SemanticKernel.Memory;
using Microsoft.SemanticKernel.Orchestration;
using SemanticKernel.Service.Model;
using SKWebApi.Skills;
using SKWebApi.Storage;

namespace CopilotChatApi.Service.Controllers;

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
            if (result.LastException is AIException aiException && aiException.Detail is not null)
            {
                return this.BadRequest(string.Concat(aiException.Message, " - Detail: " + aiException.Detail));
            }

            return this.BadRequest(result.LastErrorDescription);
        }

        return this.Ok(new AskResult { Value = result.Result, Variables = result.Variables.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)) });
    }

    [Route("bot/import")]
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<string> Import([FromServices] Kernel kernel, [FromBody] string serializedBot)
    {
        this._logger.LogDebug("Received call to import a bot");


        return $"import a bot. {serializedBot}";
    }

    [Route("bot/export")]
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<string>> ExportAsync([FromServices] Kernel kernel)
    {
        this._logger.LogDebug("Received call to export a bot");
        var memory = await GetAllMemoriesAsync(kernel.Memory).ToArrayAsync();

        return JsonSerializer.Serialize(memory);
    }

    /// <summary>
    /// Get all chat messages from memory.
    /// </summary>
    /// <param name="memory">The memory object.</param>
    private static async IAsyncEnumerable<MemoryQueryResult?> GetAllMemoriesAsync(ISemanticTextMemory memory)
    {
        var allCollections = await memory.GetCollectionsAsync();
        IList<MemoryQueryResult> allChatMessageMemories = new List<MemoryQueryResult>();

        foreach (var collection in allCollections)
        {
            var results = await memory.SearchAsync(
                collection,
                "abc", // dummy query since we don't care about relevance. An empty string will cause exception.
                limit: 1,
                minRelevanceScore: 0.0, // no relevance required since the collection only has one entry
                cancel: default
            ).ToListAsync();
            allChatMessageMemories.Add(results.First());
        }

        foreach (var item in allChatMessageMemories.OrderBy(item => item.Metadata.Id))
        {
            yield return item;
        }
    }
}
