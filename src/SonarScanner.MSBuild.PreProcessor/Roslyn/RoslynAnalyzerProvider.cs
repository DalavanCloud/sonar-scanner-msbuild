﻿/*
 * SonarScanner for MSBuild
 * Copyright (C) 2016-2018 SonarSource SA
 * mailto:info AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SonarScanner.MSBuild.Common;
using SonarScanner.MSBuild.PreProcessor.Roslyn.Model;
using SonarScanner.MSBuild.TFS;

namespace SonarScanner.MSBuild.PreProcessor.Roslyn
{
    public class RoslynAnalyzerProvider : IAnalyzerProvider
    {
        public const string RoslynFormatNamePrefix = "roslyn-{0}";
        public const string RoslynRulesetFileName = "SonarQubeRoslyn-{0}.ruleset";

        private const string SONARANALYZER_PARTIAL_REPO_KEY_PREFIX = "sonaranalyzer-";
        private const string SONARANALYZER_PARTIAL_REPO_KEY = SONARANALYZER_PARTIAL_REPO_KEY_PREFIX + "{0}";
        private const string ROSLYN_REPOSITORY_PREFIX = "roslyn.";

        public const string CSharpLanguage = "cs";
        public const string CSharpPluginKey = "csharp";
        public const string CSharpRepositoryKey = "csharp";

        public const string VBNetLanguage = "vbnet";
        public const string VBNetPluginKey = "vbnet";
        public const string VBNetRepositoryKey = "vbnet";

        private readonly IAnalyzerInstaller analyzerInstaller;
        private readonly ILogger logger;
        private TeamBuildSettings sqSettings;
        private IDictionary<string, string> sqServerSettings;

        #region Public methods

        public RoslynAnalyzerProvider(IAnalyzerInstaller analyzerInstaller, ILogger logger)
        {
            this.analyzerInstaller = analyzerInstaller ?? throw new ArgumentNullException(nameof(analyzerInstaller));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public AnalyzerSettings SetupAnalyzer(TeamBuildSettings settings, IDictionary<string, string> serverSettings,
            IEnumerable<SonarRule> activeRules, IEnumerable<SonarRule> inactiveRules, string language)
        {
            this.sqSettings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.sqServerSettings = serverSettings ?? throw new ArgumentNullException(nameof(serverSettings));
            if (language == null)
            {
                throw new ArgumentNullException(nameof(language));
            }

            if (inactiveRules == null)
            {
                throw new ArgumentNullException(nameof(inactiveRules));
            }
            if (activeRules == null)
            {
                throw new ArgumentNullException(nameof(activeRules));
            }

            var analyzer = ConfigureAnalyzer(language, activeRules, inactiveRules);
            if (analyzer == null)
            {
                this.logger.LogInfo(Resources.RAP_NoPluginInstalled, language);
            }

            return analyzer;
        }

        public static string GetRoslynFormatName(string language)
        {
            return string.Format(RoslynFormatNamePrefix, language);
        }

        public static string GetRoslynRulesetFileName(string language)
        {
            return string.Format(RoslynRulesetFileName, language);
        }

        #endregion Public methods

        #region Private methods

        /// <summary>
        /// Generates several files related to rulesets and roslyn analyzer assemblies.
        /// Active rules should never be empty, but depending on the server settings of repo keys, we might have no rules in the ruleset.
        /// In that case, this method returns null.
        /// </summary>
        private AnalyzerSettings ConfigureAnalyzer(string language, IEnumerable<SonarRule> activeRules, IEnumerable<SonarRule> inactiveRules)
        {
            var ruleSetGenerator = new RoslynRuleSetGenerator(this.sqServerSettings);
            var ruleSet = ruleSetGenerator.Generate(activeRules, inactiveRules, language);
            var rulesetFilePath = WriteRuleset(ruleSet, language);
            if (rulesetFilePath == null)
            {
                // no ruleset, nothing was written in disk
                return null;
            }

            var additionalFiles = WriteAdditionalFiles(language, activeRules);
            var analyzersAssemblies = FetchAnalyzerAssemblies(activeRules, language);

            var compilerConfig = new AnalyzerSettings(language, rulesetFilePath,
                analyzersAssemblies ?? Enumerable.Empty<string>(),
                additionalFiles ?? Enumerable.Empty<string>());
            return compilerConfig;
        }

        /// <summary>
        /// Write ruleset to a file.
        /// Nothing will be written and null with be returned if the ruleset contains no rules
        /// </summary>
        public string WriteRuleset(RuleSet ruleSet, string language)
        {
            string rulesetFilePath = null;
            if (ruleSet == null || ruleSet.Rules == null)
            {
                this.logger.LogDebug(Resources.RAP_ProfileDoesNotContainRuleset);
            }
            else
            {
                rulesetFilePath = GetRulesetFilePath(this.sqSettings, language);
                this.logger.LogDebug(Resources.RAP_UnpackingRuleset, rulesetFilePath);
                ruleSet.Save(rulesetFilePath);
            }
            return rulesetFilePath;
        }

        private static string GetRulesetFilePath(TeamBuildSettings settings, string language)
        {
            return Path.Combine(settings.SonarConfigDirectory, GetRoslynRulesetFileName(language));
        }

        private IEnumerable<string> WriteAdditionalFiles(string language, IEnumerable<SonarRule> activeRules)
        {
            Debug.Assert(activeRules != null, "Supplied active rules should not be null");

            var additionalFiles = new List<string>();
            var filePath = WriteSonarLintXmlFile(language, activeRules);
            if (filePath != null)
            {
                Debug.Assert(File.Exists(filePath), "Expecting the additional file to exist: {0}", filePath);
                additionalFiles.Add(filePath);
            }

            return additionalFiles;
        }

        private string WriteSonarLintXmlFile(string language, IEnumerable<SonarRule> activeRules)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                this.logger.LogDebug(Resources.RAP_AdditionalFileNameMustBeSpecified);
                return null;
            }

            string content;
            if (language.Equals(CSharpLanguage))
            {
                content = RoslynSonarLint.GenerateXml(activeRules, this.sqServerSettings, language, "csharpsquid");
            }
            else
            {
                content = RoslynSonarLint.GenerateXml(activeRules, this.sqServerSettings, language, "vbnet");
            }

            var langDir = Path.Combine(this.sqSettings.SonarConfigDirectory, language);
            Directory.CreateDirectory(langDir);

            var fullPath = Path.Combine(langDir, "SonarLint.xml");
            if (File.Exists(fullPath))
            {
                this.logger.LogDebug(Resources.RAP_AdditionalFileAlreadyExists, language, fullPath);
                return null;
            }

            this.logger.LogDebug(Resources.RAP_WritingAdditionalFile, fullPath);
            File.WriteAllText(fullPath, content);
            return fullPath;
        }

        public IEnumerable<string> FetchAnalyzerAssemblies(IEnumerable<SonarRule> activeRules, string language)
        {
            var repoKeys = ActiveRulesPartialRepoKey(activeRules, language);
            IList<Plugin> plugins = new List<Plugin>();

            foreach (var repoKey in repoKeys)
            {
                if (!this.sqServerSettings.TryGetValue(PluginKeyPropertyKey(repoKey), out var pluginkey)
                    || !this.sqServerSettings.TryGetValue(PluginVersionPropertyKey(repoKey), out var pluginVersion)
                    || !this.sqServerSettings.TryGetValue(StaticResourceNamePropertyKey(repoKey), out var staticResourceName))
                {
                    if (!repoKey.StartsWith(SONARANALYZER_PARTIAL_REPO_KEY_PREFIX))
                    {
                        this.logger.LogInfo(Resources.RAP_NoAssembliesForRepo, repoKey, language);
                    }
                    continue;
                }

                plugins.Add(new Plugin(pluginkey, pluginVersion, staticResourceName));
            }

            IEnumerable<string> analyzerAssemblyPaths = null;
            if (plugins.Count == 0)
            {
                this.logger.LogInfo(Resources.RAP_NoAnalyzerPluginsSpecified, language);
            }
            else
            {
                this.logger.LogInfo(Resources.RAP_ProvisioningAnalyzerAssemblies, language);
                analyzerAssemblyPaths = this.analyzerInstaller.InstallAssemblies(plugins);
            }
            return analyzerAssemblyPaths;
        }

        private static string PluginKeyPropertyKey(string partialRepoKey)
        {
            return partialRepoKey + ".pluginKey";
        }

        private static string PluginVersionPropertyKey(string partialRepoKey)
        {
            return partialRepoKey + ".pluginVersion";
        }

        private static string StaticResourceNamePropertyKey(string partialRepoKey)
        {
            return partialRepoKey + ".staticResourceName";
        }

        private static ICollection<string> ActiveRulesPartialRepoKey(IEnumerable<SonarRule> activeRules, string language)
        {
            var list = new HashSet<string>
            {
                // Always add SonarC# and SonarVB to have at least tokens...
                string.Format(SONARANALYZER_PARTIAL_REPO_KEY, "cs"),
                string.Format(SONARANALYZER_PARTIAL_REPO_KEY, "vbnet")
            };

            foreach (var activeRule in activeRules)
            {
                if (activeRule.RepoKey.StartsWith(ROSLYN_REPOSITORY_PREFIX))
                {
                    list.Add(activeRule.RepoKey.Substring(ROSLYN_REPOSITORY_PREFIX.Length));
                }
            }

            return list;
        }

        #endregion Private methods
    }
}
