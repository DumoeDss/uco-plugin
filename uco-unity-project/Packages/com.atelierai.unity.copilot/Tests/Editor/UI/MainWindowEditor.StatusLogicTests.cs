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
using com.AtelierAI.Unity.Copilot.Editor.Services;
using com.AtelierAI.Unity.Copilot.Editor.UI;
using com.AtelierAI.Uco.Framework;
using NUnit.Framework;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    public class MainWindowEditorStatusLogicTests
    {
        #region GetConnectionStatusClass

        [TestCase(ConnectionState.Connected, true, MainWindowEditor.USS_Connected)]
        [TestCase(ConnectionState.Connected, false, MainWindowEditor.USS_Disconnected)]
        [TestCase(ConnectionState.Disconnected, true, MainWindowEditor.USS_Connecting)]
        [TestCase(ConnectionState.Disconnected, false, MainWindowEditor.USS_Disconnected)]
        [TestCase(ConnectionState.Reconnecting, true, MainWindowEditor.USS_Connecting)]
        [TestCase(ConnectionState.Reconnecting, false, MainWindowEditor.USS_Disconnected)]
        public void GetConnectionStatusClass(ConnectionState state, bool keepConnected, string expected)
        {
            Assert.AreEqual(expected, MainWindowEditor.GetConnectionStatusClass(state, keepConnected));
        }

        #endregion

        #region GetConnectionStatusText

        [TestCase(ConnectionState.Connected, true, "Connected")]
        [TestCase(ConnectionState.Connected, false, "Disconnected")]
        [TestCase(ConnectionState.Disconnected, true, "Connecting...")]
        [TestCase(ConnectionState.Disconnected, false, "Disconnected")]
        [TestCase(ConnectionState.Reconnecting, true, "Connecting...")]
        [TestCase(ConnectionState.Reconnecting, false, "Disconnected")]
        public void GetConnectionStatusText(ConnectionState state, bool keepConnected, string expected)
        {
            Assert.AreEqual(expected, MainWindowEditor.GetConnectionStatusText(state, keepConnected));
        }

        #endregion

        #region GetButtonText

        [TestCase(ConnectionState.Connected, true, "Disconnect")]
        [TestCase(ConnectionState.Connected, false, "Connect")]
        [TestCase(ConnectionState.Disconnected, true, "Stop")]
        [TestCase(ConnectionState.Disconnected, false, "Connect")]
        [TestCase(ConnectionState.Reconnecting, true, "Stop")]
        [TestCase(ConnectionState.Reconnecting, false, "Connect")]
        public void GetButtonText(ConnectionState state, bool keepConnected, string expected)
        {
            Assert.AreEqual(expected, MainWindowEditor.GetButtonText(state, keepConnected));
        }

        #endregion

        #region GetServerButtonText

        [TestCase(CopilotServerStatus.Running, "Stop")]
        [TestCase(CopilotServerStatus.Starting, "Starting...")]
        [TestCase(CopilotServerStatus.Stopping, "Stopping...")]
        [TestCase(CopilotServerStatus.Stopped, "Start")]
        [TestCase(CopilotServerStatus.External, "External")]
        public void GetServerButtonText(CopilotServerStatus status, string expected)
        {
            Assert.AreEqual(expected, MainWindowEditor.GetServerButtonText(status));
        }

        #endregion

        #region GetServerStatusClass

        [TestCase(CopilotServerStatus.Running, MainWindowEditor.USS_Connected)]
        [TestCase(CopilotServerStatus.Starting, MainWindowEditor.USS_Connecting)]
        [TestCase(CopilotServerStatus.Stopping, MainWindowEditor.USS_Connecting)]
        [TestCase(CopilotServerStatus.Stopped, MainWindowEditor.USS_Disconnected)]
        [TestCase(CopilotServerStatus.External, MainWindowEditor.USS_External)]
        public void GetServerStatusClass(CopilotServerStatus status, string expected)
        {
            Assert.AreEqual(expected, MainWindowEditor.GetServerStatusClass(status));
        }

        #endregion

        #region IsServerButtonEnabled

        [TestCase(CopilotServerStatus.Running, true)]
        [TestCase(CopilotServerStatus.Starting, false)]
        [TestCase(CopilotServerStatus.Stopping, false)]
        [TestCase(CopilotServerStatus.Stopped, true)]
        [TestCase(CopilotServerStatus.External, false)]
        public void IsServerButtonEnabled(CopilotServerStatus status, bool expected)
        {
            Assert.AreEqual(expected, MainWindowEditor.IsServerButtonEnabled(status));
        }

        #endregion

        #region CombineCopilotServerStatus

        [TestCase(CopilotServerStatus.Stopped, true, CopilotServerStatus.External)]
        [TestCase(CopilotServerStatus.Starting, true, CopilotServerStatus.External)]
        [TestCase(CopilotServerStatus.Running, true, CopilotServerStatus.Running)]
        [TestCase(CopilotServerStatus.Stopped, false, CopilotServerStatus.Stopped)]
        [TestCase(CopilotServerStatus.Running, false, CopilotServerStatus.Running)]
        [TestCase(CopilotServerStatus.Stopping, true, CopilotServerStatus.External)]
        [TestCase(CopilotServerStatus.Stopping, false, CopilotServerStatus.Stopping)]
        public void CombineCopilotServerStatus(CopilotServerStatus status, bool isConnected, CopilotServerStatus expected)
        {
            Assert.AreEqual(expected, MainWindowEditor.CombineCopilotServerStatus(status, isConnected));
        }

        #endregion

        #region GetServerLabelText

        [Test]
        public void GetServerLabelText_Running()
        {
            Assert.AreEqual("Server: Running", MainWindowEditor.GetServerLabelText(CopilotServerStatus.Running));
        }

        [Test]
        public void GetServerLabelText_Starting()
        {
            Assert.AreEqual("Server: Starting...", MainWindowEditor.GetServerLabelText(CopilotServerStatus.Starting));
        }

        [Test]
        public void GetServerLabelText_Stopping()
        {
            Assert.AreEqual("Server: Stopping...", MainWindowEditor.GetServerLabelText(CopilotServerStatus.Stopping));
        }

        [Test]
        public void GetServerLabelText_Stopped()
        {
            Assert.AreEqual("Server", MainWindowEditor.GetServerLabelText(CopilotServerStatus.Stopped));
        }

        [Test]
        public void GetServerLabelText_External()
        {
            Assert.AreEqual("Server: External", MainWindowEditor.GetServerLabelText(CopilotServerStatus.External));
        }

        #endregion




        #region IsHostFieldReadOnly

        [TestCase(true, ConnectionState.Disconnected, true)]
        [TestCase(true, ConnectionState.Connected, true)]
        [TestCase(true, ConnectionState.Reconnecting, true)]
        [TestCase(false, ConnectionState.Disconnected, false)]
        [TestCase(false, ConnectionState.Connected, true)]
        [TestCase(false, ConnectionState.Reconnecting, true)]
        public void IsHostFieldReadOnly(bool keepConnected, ConnectionState state, bool expected)
        {
            Assert.AreEqual(expected, MainWindowEditor.IsHostFieldReadOnly(keepConnected, state));
        }

        #endregion
    }
}
