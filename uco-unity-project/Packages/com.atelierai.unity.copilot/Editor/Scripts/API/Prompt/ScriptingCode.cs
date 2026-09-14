/*
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)             │
│  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)    │
│  Copyright (c) 2025 Ivan Murzak                                  │
│  Licensed under the Apache License, Version 2.0.                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using System.ComponentModel;
using com.AtelierAI.Uco.Framework;
using com.AtelierAI.Uco.Framework.Common.Model;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    [UcoPromptType]
    public partial class Prompt_ScriptingCode
    {
        [UcoPrompt(Name = "generate-monobehaviour-template", Role = Role.User, Enabled = false)]
        [Description("Create a MonoBehaviour script template with common Unity lifecycle methods and patterns.")]
        public string GenerateMonoBehaviourTemplate()
        {
            return "Create new MonoBehaviour script with Start, Update, and other common Unity lifecycle methods, including proper using statements and namespace.";
        }

        [UcoPrompt(Name = "add-event-system", Role = Role.User, Enabled = false)]
        [Description("Implement UnityEvent-based communication system between GameObjects.")]
        public string AddEventSystem()
        {
            return "Create event system using UnityEvents, UnityActions, or custom event delegates for decoupled communication between game systems and components.";
        }

        [UcoPrompt(Name = "create-singleton-manager", Role = Role.User, Enabled = false)]
        [Description("Generate a singleton manager class following Unity best practices.")]
        public string CreateSingletonManager()
        {
            return "Create singleton manager class with proper initialization, thread-safe implementation, and DontDestroyOnLoad setup for persistent game managers.";
        }

        [UcoPrompt(Name = "setup-coroutine-framework", Role = Role.User, Enabled = false)]
        [Description("Add coroutine-based asynchronous operations and utility methods.")]
        public string SetupCoroutineFramework()
        {
            return "Create coroutine utilities for async operations like delayed execution, gradual value changes, and time-based operations with proper cleanup.";
        }

        [UcoPrompt(Name = "create-scriptableobject-data", Role = Role.User, Enabled = false)]
        [Description("Generate ScriptableObject classes for data storage and configuration.")]
        public string CreateScriptableObjectData()
        {
            return "Create ScriptableObject classes for game data, settings, and configuration with proper CreateAssetMenu attributes and serialization.";
        }

        [UcoPrompt(Name = "implement-object-pooling", Role = Role.User, Enabled = false)]
        [Description("Create object pooling system for performance optimization.")]
        public string ImplementObjectPooling()
        {
            return "Implement object pooling system for frequently instantiated/destroyed objects like bullets, enemies, or particles to improve performance.";
        }

        [UcoPrompt(Name = "add-state-machine", Role = Role.User, Enabled = false)]
        [Description("Create a finite state machine for character or game state management.")]
        public string AddStateMachine()
        {
            return "Create finite state machine implementation for managing character states, game states, or AI behavior with proper state transitions.";
        }

        [UcoPrompt(Name = "setup-dependency-injection", Role = Role.User, Enabled = false)]
        [Description("Implement simple dependency injection pattern for better code organization.")]
        public string SetupDependencyInjection()
        {
            return "Create simple dependency injection system using constructor injection or service locator pattern for better testability and loose coupling.";
        }
    }
}