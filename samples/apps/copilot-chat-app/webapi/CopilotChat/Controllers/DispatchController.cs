// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Orchestration;
using Microsoft.SemanticKernel.Planning;
using SemanticKernel.Service.CopilotChat.Models;
using SemanticKernel.Service.CopilotChat.Options;
using SemanticKernel.Service.CopilotChat.Skills;
using SemanticKernel.Service.CopilotChat.Skills.ChatSkills;
using SemanticKernel.Service.CopilotChat.Storage;
using SemanticKernel.Service.Models;
using SemanticKernel.Service.Options;

namespace SemanticKernel.Service.CopilotChat.Controllers;

[ApiController]
public class DispatchController : ControllerBase, IDisposable
{
    private readonly ILogger<DispatchController> _logger;
    private readonly IKernel _kernel;
    private readonly SequentialPlanner _planner;

    public DispatchController(
        IKernel kernel,
        IOptions<AIServiceOptions> aiServiceOptions,
        ChatMessageRepository chatMessageRepository,
        ChatSessionRepository chatSessionRepository,
        IOptions<PromptsOptions> promptOptions,
        IOptions<DocumentMemoryOptions> documentImportOptions,
        CopilotChatPlanner planner,
        ILogger<DispatchController> logger)
    {
        /* This way to create a new kernel will throws argument exception (logger is null) when importing skills.
         * Question: Why?
            this._kernel = new Kernel(
                    new SkillCollection(),
                    kernel.PromptTemplateEngine,
                    kernel.Memory,
                    this.CreateKernelConfig(aiServiceOptions.Value),
                    this._logger!);
        */

        this._kernel = new KernelBuilder()
                .WithLogger(logger)
                .WithMemory(kernel.Memory)
                .WithConfiguration(this.CreateKernelConfig(aiServiceOptions.Value))
                .Build();

        this._logger = logger;
        this._planner = new SequentialPlanner(this._kernel);

        // import skills
        // Chat skill
        this._kernel.ImportSkill(new ChatSkill(
                kernel: kernel,
                chatMessageRepository: chatMessageRepository,
                chatSessionRepository: chatSessionRepository,
                promptOptions: promptOptions,
                documentImportOptions: documentImportOptions,
                planner: planner,
                logger: logger),
            nameof(ChatSkill));

        // Dispatch skill
        this._kernel.ImportSkill(new DispatchSkills(), nameof(DispatchSkills));
    }

    [Authorize]
    [Route("dispatch")]
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DispatchAsync(
        [FromServices] CopilotChatPlanner planner,
        [FromBody] Ask ask,
        [FromHeader] OpenApiSkillsAuthHeaders openApiSkillsAuthHeaders)
    {
        this._logger.LogDebug("Dispatch request received.");

        // Put ask's variables in the context we will use.
        var contextVariables = new ContextVariables(ask.Input);
        foreach (var input in ask.Variables)
        {
            contextVariables.Set(input.Key, input.Value);
        }

        // Create a sequential planner with proper skill scope. It will figure out which skill to call next.
        // TODO: Would it be annoying in this use case if ask user confirmation everytime?
        // Question: When to clone a context, when not to?

        try
        {
            var plan = await this._planner.CreatePlanAsync(ask.Input);
            // contextVariables.Set("action", plan.ToJson());
            Console.WriteLine($"{plan.ToJson(true)}");
            // contextVariables.Update(plan.ToJson(true));

            // TODO: Invoke the plan.
            /* 
            if (action.Contains('.', StringComparison.Ordinal))
            {
                var parts = action.Split('.');
                functionOrPlan = context.Skills!.GetFunction(parts[0], parts[1]);
            }
            else
            {
                functionOrPlan = context.Skills!.GetFunction(action);
            }
            */

            // TODO: Do we need to store the plan in memory?
            /*await context.Memory.SaveInformationAsync(
                collection: $"{chatId}-LearningSkill.LessonPlans",
                text: plan.ToJson(true),
                id: Guid.NewGuid().ToString(),
                description: $"Plan for '{ask.Input}'",
                additionalMetadata: plan.ToJson());*/

            return this.Ok(plan.ToJson(true));
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception e)
        {
            Console.WriteLine($"*understands* I couldn't create a plan for '{ask.Input}'");
            Console.WriteLine(e);

            return this.BadRequest(e.Message);
        }
#pragma warning restore CA1031 // Do not catch general exception types
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        this.Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Dispose of the object.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Question: IKernel doesn't have Dispose(), while Kernel is disposable?!
            // this._kernel.Dispose();
        }
    }

    // Temp hack to quickly setup a sperated kernel for this controller
    private KernelConfig CreateKernelConfig(AIServiceOptions options)
    {
        var kernelConfig = new KernelConfig();

        if (options.Type == AIServiceOptions.AIServiceType.AzureOpenAI)
        {
            kernelConfig.AddAzureChatCompletionService(options.Models.Completion, options.Endpoint, options.Key);
            kernelConfig.AddAzureTextEmbeddingGenerationService(options.Models.Embedding, options.Endpoint, options.Key);
        }
        else if (options.Type == AIServiceOptions.AIServiceType.OpenAI)
        {
            kernelConfig.AddOpenAIChatCompletionService(options.Models.Completion, options.Key);
            kernelConfig.AddOpenAITextEmbeddingGenerationService(options.Models.Embedding, options.Key);
        }
        else
        {
            throw new ArgumentException($"Invalid {nameof(options.Type)} value in '{AIServiceOptions.PropertyName}' settings.");
        }

        return kernelConfig;
    }
}
