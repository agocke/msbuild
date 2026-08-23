// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Build.Construction;
using Microsoft.Build.Engine.UnitTests;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Shared.FileSystem;
using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests.Evaluation
{
    public class ConditionEvaluator_Tests
    {
        [Fact]
        public async Task ExpressionPoolGrowsUnderConcurrentDemand()
        {
            const int evaluationCount = 8;
            const string propertyName = "BlockingProperty";
            string propertyValue = Guid.NewGuid().ToString("N");
            string condition = $"'$({propertyName})' == '{propertyValue}'";

            using var allEvaluationsBlocked = new CountdownEvent(evaluationCount);
            using var releaseEvaluations = new ManualResetEventSlim();

            Task<bool>[] evaluations = Enumerable.Range(0, evaluationCount)
                .Select(_ => Task.Factory.StartNew(
                    () =>
                    {
                        var propertyProvider = new BlockingPropertyProvider(
                            propertyName,
                            propertyValue,
                            allEvaluationsBlocked,
                            releaseEvaluations);
                        var expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(
                            propertyProvider,
                            FileSystems.Default);

                        return ConditionEvaluator.EvaluateCondition(
                            condition,
                            ParserOptions.AllowAll,
                            expander,
                            ExpanderOptions.ExpandProperties,
                            Environment.CurrentDirectory,
                            MockElementLocation.Instance,
                            FileSystems.Default,
                            loggingContext: null);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .ToArray();

            try
            {
                allEvaluationsBlocked.Wait(TimeSpan.FromSeconds(10))
                    .ShouldBeTrue("All evaluators should acquire distinct expression trees.");
            }
            finally
            {
                releaseEvaluations.Set();
            }

            Assert.All(await Task.WhenAll(evaluations), Assert.True);
        }

        private sealed class BlockingPropertyProvider : IPropertyProvider<ProjectPropertyInstance>
        {
            private readonly ProjectPropertyInstance _property;
            private readonly CountdownEvent _allEvaluationsBlocked;
            private readonly ManualResetEventSlim _releaseEvaluations;
            private int _hasBlocked;

            public BlockingPropertyProvider(
                string propertyName,
                string propertyValue,
                CountdownEvent allEvaluationsBlocked,
                ManualResetEventSlim releaseEvaluations)
            {
                _property = ProjectPropertyInstance.Create(propertyName, propertyValue);
                _allEvaluationsBlocked = allEvaluationsBlocked;
                _releaseEvaluations = releaseEvaluations;
            }

            public ProjectPropertyInstance GetProperty(string name)
            {
                return string.Equals(name, _property.Name, StringComparison.OrdinalIgnoreCase)
                    ? GetProperty()
                    : null!;
            }

            public ProjectPropertyInstance GetProperty(string name, int startIndex, int endIndex)
            {
                return endIndex - startIndex + 1 == _property.Name.Length &&
                       string.Compare(
                           name,
                           startIndex,
                           _property.Name,
                           0,
                           _property.Name.Length,
                           StringComparison.OrdinalIgnoreCase) == 0
                    ? GetProperty()
                    : null!;
            }

            private ProjectPropertyInstance GetProperty()
            {
                if (Interlocked.Exchange(ref _hasBlocked, 1) == 0)
                {
                    _allEvaluationsBlocked.Signal();
                    _releaseEvaluations.Wait();
                }

                return _property;
            }
        }
    }
}
