/*
+-----------------------------------------------------------------+
|  Author: Ivan Murzak (https://github.com/IvanMurzak)             |
|  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)    |
|  Copyright (c) 2025 Ivan Murzak                                  |
|  Licensed under the Apache License, Version 2.0.                 |
|  See the LICENSE file in the project root for more information.  |
+-----------------------------------------------------------------+
*/

#nullable enable
using System;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using NUnit.Framework;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    [TestFixture]
    public sealed class PlayerPrefsValuesTests
    {
        string _logicalKey = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _logicalKey = "Unity_Copilot_PlayerPrefsValuesTests_" + Guid.NewGuid().ToString("N");
            DeleteFixtureKeys();
        }

        [TearDown]
        public void TearDown()
        {
            DeleteFixtureKeys();
        }

        [Test]
        public void MissingKeys_ReturnConfiguredDefaultsAndExposeMetadata()
        {
            var boolean = new PlayerPrefsBool(_logicalKey, true);
            var text = new PlayerPrefsString(_logicalKey, "fallback");
            var integer = new PlayerPrefsInt(_logicalKey, -42);

            Assert.That(boolean.Key, Is.EqualTo(_logicalKey));
            Assert.That(boolean.InternalKey, Is.EqualTo("Boolean:" + _logicalKey));
            Assert.That(boolean.DefaultValue, Is.True);
            Assert.That(boolean.Value, Is.True);

            Assert.That(text.Key, Is.EqualTo(_logicalKey));
            Assert.That(text.InternalKey, Is.EqualTo("String:" + _logicalKey));
            Assert.That(text.DefaultValue, Is.EqualTo("fallback"));
            Assert.That(text.Value, Is.EqualTo("fallback"));

            Assert.That(integer.Key, Is.EqualTo(_logicalKey));
            Assert.That(integer.InternalKey, Is.EqualTo("Int32:" + _logicalKey));
            Assert.That(integer.DefaultValue, Is.EqualTo(-42));
            Assert.That(integer.Value, Is.EqualTo(-42));
        }

        [Test]
        public void RoundTrips_PreserveEdgeAndNonDefaultValues()
        {
            var boolean = new PlayerPrefsBool(_logicalKey, true);
            var text = new PlayerPrefsString(_logicalKey, "fallback");
            var integer = new PlayerPrefsInt(_logicalKey, 7);

            boolean.Value = false;
            text.Value = string.Empty;
            integer.Value = -123;

            Assert.That(boolean.Value, Is.False);
            Assert.That(PlayerPrefs.GetInt(boolean.InternalKey, -1), Is.Zero);
            Assert.That(text.Value, Is.Empty);
            Assert.That(PlayerPrefs.GetString(text.InternalKey, "missing"), Is.Empty);
            Assert.That(integer.Value, Is.EqualTo(-123));

            boolean.Value = true;
            text.Value = "stored";
            integer.Value = 99;

            Assert.That(boolean.Value, Is.True);
            Assert.That(PlayerPrefs.GetInt(boolean.InternalKey, -1), Is.EqualTo(1));
            Assert.That(text.Value, Is.EqualTo("stored"));
            Assert.That(integer.Value, Is.EqualTo(99));
        }

        [Test]
        public void LiteralLegacyKeys_AreReadAndUpdatedInPlace()
        {
            var booleanKey = "Boolean:" + _logicalKey;
            var stringKey = "String:" + _logicalKey;
            var integerKey = "Int32:" + _logicalKey;
            PlayerPrefs.SetInt(booleanKey, 1);
            PlayerPrefs.SetString(stringKey, "legacy");
            PlayerPrefs.SetInt(integerKey, -17);

            var boolean = new PlayerPrefsBool(_logicalKey, false);
            var text = new PlayerPrefsString(_logicalKey, "fallback");
            var integer = new PlayerPrefsInt(_logicalKey, 42);

            Assert.That(boolean.Value, Is.True);
            Assert.That(text.Value, Is.EqualTo("legacy"));
            Assert.That(integer.Value, Is.EqualTo(-17));
            Assert.That(PlayerPrefs.HasKey(_logicalKey), Is.False,
                "Compatibility reads must not create an unprefixed migration key.");

            boolean.Value = false;
            text.Value = "updated";
            integer.Value = 123;

            Assert.That(PlayerPrefs.GetInt(booleanKey, -1), Is.Zero);
            Assert.That(PlayerPrefs.GetString(stringKey, string.Empty), Is.EqualTo("updated"));
            Assert.That(PlayerPrefs.GetInt(integerKey, -1), Is.EqualTo(123));
        }

        [Test]
        public void SameLogicalKey_UsesIndependentTypedEntries()
        {
            var boolean = new PlayerPrefsBool(_logicalKey);
            var text = new PlayerPrefsString(_logicalKey);
            var integer = new PlayerPrefsInt(_logicalKey);

            boolean.Value = true;
            text.Value = "isolated";
            integer.Value = -8;

            Assert.That(boolean.Value, Is.True);
            Assert.That(text.Value, Is.EqualTo("isolated"));
            Assert.That(integer.Value, Is.EqualTo(-8));

            boolean.Value = false;
            Assert.That(text.Value, Is.EqualTo("isolated"));
            Assert.That(integer.Value, Is.EqualTo(-8));
        }

        void DeleteFixtureKeys()
        {
            if (string.IsNullOrEmpty(_logicalKey))
                return;

            PlayerPrefs.DeleteKey("Boolean:" + _logicalKey);
            PlayerPrefs.DeleteKey("String:" + _logicalKey);
            PlayerPrefs.DeleteKey("Int32:" + _logicalKey);
            PlayerPrefs.DeleteKey(_logicalKey);
        }
    }
}
