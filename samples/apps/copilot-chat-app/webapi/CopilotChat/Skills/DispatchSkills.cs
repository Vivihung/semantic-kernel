// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.Orchestration;
using Microsoft.SemanticKernel.SkillDefinition;

namespace SemanticKernel.Service.CopilotChat.Skills;

public class DispatchSkills
{
    [SKFunction("Understand an image")]
    [SKFunctionName("UnderstandImage")]
    public async Task<SKContext> UnderstandImageAsync(SKContext context)
    {
        Console.WriteLine("Understand image.");

        return await Task.FromResult<SKContext>(context);
    }

    [SKFunction("Rendering an impage")]
    [SKFunctionName("RenderingImage")]
    public async Task<SKContext> RenderingImageAsync(SKContext context)
    {
        Console.WriteLine("Rendering an impage.");

        return await Task.FromResult<SKContext>(context);
    }

    [SKFunction("Rendering a text")]
    [SKFunctionName("RenderingText")]
    public async Task<SKContext> RenderingTextAsync(SKContext context)
    {
        Console.WriteLine("Rendering a text.");

        return await Task.FromResult<SKContext>(context);
    }

    [SKFunction("Rendering a rich text")]
    [SKFunctionName("RenderingRichText")]
    public async Task<SKContext> RenderingRichTextAsync(SKContext context)
    {
        Console.WriteLine("Rendering a rich text.");

        return await Task.FromResult<SKContext>(context);
    }

    [SKFunction("Rendering a person")]
    [SKFunctionName("RenderingPerson")]
    public async Task<SKContext> RenderingPersonAsync(SKContext context)
    {
        Console.WriteLine("Rendering a person.");

        return await Task.FromResult<SKContext>(context);
    }
}
