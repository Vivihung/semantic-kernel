// Copyright (c) Microsoft. All rights reserved.

using System.Threading.Tasks;
using System;
using Microsoft.SemanticKernel.SkillDefinition;
using Microsoft.SemanticKernel.Orchestration;

namespace SemanticKernel.Service.CopilotChat.Skills;

public class ImageSkills
{
    [SKFunction("When user input an image, provide a text description of the image.")]
    [SKFunctionName("UnderstandImage")]
    public async Task<SKContext> UnderstandImageAsync(SKContext context)
    {
        Console.WriteLine("Understand image.");

        return await Task.FromResult<SKContext>(context);
    }

    [SKFunction("Generate or create an image")]
    [SKFunctionName("GenerateImage")]
    public async Task<SKContext> GenerateImageAsync(SKContext context)
    {
        Console.WriteLine("Generate an image.");

        return await Task.FromResult<SKContext>(context);
    }
}
