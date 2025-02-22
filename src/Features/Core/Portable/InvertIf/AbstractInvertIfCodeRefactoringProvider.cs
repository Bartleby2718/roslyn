﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using static Microsoft.CodeAnalysis.SyntaxNodeExtensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.InvertIf;

internal abstract partial class AbstractInvertIfCodeRefactoringProvider<
    TSyntaxKind, TStatementSyntax, TIfStatementSyntax, TEmbeddedStatement> : CodeRefactoringProvider
    where TSyntaxKind : struct, Enum
    where TStatementSyntax : SyntaxNode
    where TIfStatementSyntax : TStatementSyntax
{
    private enum InvertIfStyle
    {
        IfWithElse_SwapIfBodyWithElseBody,
        IfWithoutElse_SwapIfBodyWithSubsequentStatements,
        IfWithoutElse_MoveSubsequentStatementsToIfBody,
        IfWithoutElse_WithElseClause,
        IfWithoutElse_MoveIfBodyToElseClause,
        IfWithoutElse_WithSubsequentExitPointStatement,
        IfWithoutElse_WithNearmostJumpStatement,
        IfWithoutElse_WithNegatedCondition,
    }

    protected abstract string GetTitle();

    protected abstract SyntaxList<TStatementSyntax> GetStatements(SyntaxNode node);
    protected abstract TStatementSyntax? GetNextStatement(TStatementSyntax node);

    protected abstract TStatementSyntax GetJumpStatement(TSyntaxKind kind);
    protected abstract TSyntaxKind? GetJumpStatementKind(SyntaxNode node);

    protected abstract bool IsNoOpSyntaxNode(SyntaxNode node);
    protected abstract bool IsExecutableStatement(SyntaxNode node);
    protected abstract bool IsStatementContainer(SyntaxNode node);
    protected abstract bool IsSingleStatementStatementRange(StatementRange statementRange);

    protected abstract bool CanControlFlowOut(SyntaxNode node);

    protected abstract bool CanInvert(TIfStatementSyntax ifNode);
    protected abstract bool IsElseless(TIfStatementSyntax ifNode);

    protected abstract StatementRange GetIfBodyStatementRange(TIfStatementSyntax ifNode);
    protected abstract SyntaxNode GetCondition(TIfStatementSyntax ifNode);

    protected abstract IEnumerable<TStatementSyntax> UnwrapBlock(TEmbeddedStatement ifBody);
    protected abstract TEmbeddedStatement GetIfBody(TIfStatementSyntax ifNode);
    protected abstract TEmbeddedStatement GetElseBody(TIfStatementSyntax ifNode);
    protected abstract TEmbeddedStatement GetEmptyEmbeddedStatement();

    protected abstract TEmbeddedStatement AsEmbeddedStatement(
        IEnumerable<TStatementSyntax> statements,
        TEmbeddedStatement original);

    protected abstract TIfStatementSyntax UpdateIf(
        SourceText sourceText,
        TIfStatementSyntax ifNode,
        SyntaxNode condition,
        TEmbeddedStatement trueStatement,
        TEmbeddedStatement? falseStatement = default);

    protected abstract SyntaxNode WithStatements(
        SyntaxNode node,
        IEnumerable<TStatementSyntax> statements);

    public sealed override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
    {
        var (document, _, cancellationToken) = context;

        var ifNode = await context.TryGetRelevantNodeAsync<TIfStatementSyntax>().ConfigureAwait(false);
        if (ifNode == null)
            return;

        if (!CanInvert(ifNode))
            return;

        var title = GetTitle();
        context.RegisterRefactoring(
            CodeAction.Create(
                title,
                c => InvertIfAsync(document, ifNode, c),
                title),
            ifNode.Span);
    }

    private InvertIfStyle GetInvertIfStyle(
        ISyntaxKinds syntaxKinds,
        TIfStatementSyntax ifNode,
        SemanticModel semanticModel,
        out SyntaxNode? subsequentSingleExitPoint)
    {
        subsequentSingleExitPoint = null;

        if (!IsElseless(ifNode))
        {
            return InvertIfStyle.IfWithElse_SwapIfBodyWithElseBody;
        }

        var ifBodyStatementRange = GetIfBodyStatementRange(ifNode);
        if (IsEmptyStatementRange(ifBodyStatementRange))
        {
            // (1) An empty if-statement: just negate the condition
            //  
            //  if (condition) { }
            //
            // ->
            //
            //  if (!condition) { }
            //
            return InvertIfStyle.IfWithoutElse_WithNegatedCondition;
        }

        var subsequentStatementRanges = GetSubsequentStatementRanges(ifNode);
        if (subsequentStatementRanges.All(IsEmptyStatementRange))
        {
            // (2) No statements after if-statement, invert with the nearmost parent jump-statement
            //
            //  void M() {
            //    if (condition) {
            //      Body();
            //    }
            //  }
            //
            // ->
            //
            //  void M() {
            //    if (!condition) {
            //      return;
            //    }
            //    Body();
            //  }
            //
            return InvertIfStyle.IfWithoutElse_WithNearmostJumpStatement;
        }

        AnalyzeControlFlow(
            semanticModel, ifBodyStatementRange,
            out var ifBodyEndPointIsReachable,
            out var ifBodySingleExitPointOpt);

        AnalyzeSubsequentControlFlow(
            semanticModel, subsequentStatementRanges,
            out var subsequentEndPointIsReachable,
            out subsequentSingleExitPoint);

        if (subsequentEndPointIsReachable)
        {
            if (!ifBodyEndPointIsReachable)
            {
                if (IsSingleStatementStatementRange(ifBodyStatementRange) &&
                    SubsequentStatementsAreInTheSameBlock(ifNode, subsequentStatementRanges) &&
                    ifBodySingleExitPointOpt != null &&
                    GetNearestParentJumpStatementKind(ifNode).Equals(syntaxKinds.Convert<TSyntaxKind>(ifBodySingleExitPointOpt.RawKind)))
                {
                    // (3) Inverse of the case (2). Safe to move all subsequent statements to if-body.
                    // 
                    //  while (condition) {
                    //    if (condition) {
                    //      continue;
                    //    }
                    //    f();
                    //  }
                    //
                    // ->
                    //
                    //  while (condition) {
                    //    if (!condition) {
                    //      f();
                    //    }
                    //  }
                    //
                    return InvertIfStyle.IfWithoutElse_MoveSubsequentStatementsToIfBody;
                }
                else
                {
                    // (4) Otherwise, we generate the else and swap blocks to keep flow intact.
                    // 
                    //  while (condition) {
                    //    if (condition) {
                    //      return;
                    //    }
                    //    f();
                    //  }
                    //
                    // ->
                    //
                    //  while (condition) {
                    //    if (!condition) {
                    //      f();
                    //    } else {
                    //      return;
                    //    }
                    //  }
                    //
                    return InvertIfStyle.IfWithoutElse_WithElseClause;
                }
            }
        }
        else if (ifBodyEndPointIsReachable)
        {
            if (subsequentSingleExitPoint != null &&
                SingleSubsequentStatement(subsequentStatementRanges))
            {
                // (5) if-body end-point is reachable but the next statement is a only jump-statement.
                //     This usually happens in a switch-statement. We invert and use that jump-statement.
                // 
                //  case constant:
                //    if (condition) {
                //      f();
                //    }
                //    break;
                //
                // ->
                //
                //  case constant:
                //    if (!condition) {
                //      break;
                //    }
                //    f();
                //    break; // we always keep this so that we don't end up with invalid code.
                //
                return InvertIfStyle.IfWithoutElse_WithSubsequentExitPointStatement;
            }
        }
        else if (SubsequentStatementsAreInTheSameBlock(ifNode, subsequentStatementRanges))
        {
            // (6) If both if-body and subsequent statements have an unreachable end-point,
            //     it would be safe to just swap the two.
            //
            //    if (condition) {
            //      return;
            //    }
            //    break;
            //
            // ->
            //
            //  case constant:
            //    if (!condition) {
            //      break;
            //    }
            //    return;
            //
            return InvertIfStyle.IfWithoutElse_SwapIfBodyWithSubsequentStatements;
        }

        // (7) If none of the above worked, as the last resort we invert and generate an empty if-body.
        // 
        //  {
        //    if (condition) {
        //      f();
        //    }
        //    f();
        //  }
        //
        // ->
        //
        //  {
        //    if (!condition) {
        //    } else {
        //      f();
        //    }
        //    f();
        //  }
        //  
        return InvertIfStyle.IfWithoutElse_MoveIfBodyToElseClause;
    }

    private bool SingleSubsequentStatement(ImmutableArray<StatementRange> subsequentStatementRanges)
        => subsequentStatementRanges.Length == 1 && IsSingleStatementStatementRange(subsequentStatementRanges[0]);

    private async Task<Document> InvertIfAsync(
        Document document,
        TIfStatementSyntax ifNode,
        CancellationToken cancellationToken)
    {
        var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var sourceText = await document.GetValueTextAsync(cancellationToken).ConfigureAwait(false);
        var syntaxKinds = document.GetRequiredLanguageService<ISyntaxKindsService>();

        var invertIfStyle = GetInvertIfStyle(syntaxKinds, ifNode, semanticModel, out var subsequentSingleExitPoint);
        var generator = document.GetRequiredLanguageService<SyntaxGenerator>();
        return document.WithSyntaxRoot(
            GetRootWithInvertIfStatement(
                sourceText,
                root,
                ifNode,
                invertIfStyle,
                subsequentSingleExitPoint,
                negatedExpression: generator.Negate(
                    generator.SyntaxGeneratorInternal,
                    GetCondition(ifNode),
                    semanticModel,
                    cancellationToken),
                document.GetRequiredLanguageService<ISyntaxFactsService>()));
    }

    private static void AnalyzeSubsequentControlFlow(
        SemanticModel semanticModel,
        ImmutableArray<StatementRange> subsequentStatementRanges,
        out bool subsequentEndPointIsReachable,
        out SyntaxNode? subsequentSingleExitPoint)
    {
        subsequentEndPointIsReachable = true;
        subsequentSingleExitPoint = null;

        foreach (var statementRange in subsequentStatementRanges)
        {
            AnalyzeControlFlow(
                semanticModel,
                statementRange,
                out subsequentEndPointIsReachable,
                out subsequentSingleExitPoint);

            if (!subsequentEndPointIsReachable)
            {
                return;
            }
        }
    }

    private static void AnalyzeControlFlow(
        SemanticModel semanticModel,
        StatementRange statementRange,
        out bool endPointIsReachable,
        out SyntaxNode? singleExitPoint)
    {
        var flow = semanticModel.AnalyzeControlFlow(
            statementRange.FirstStatement,
            statementRange.LastStatement);

        endPointIsReachable = flow.EndPointIsReachable;
        singleExitPoint = flow.ExitPoints.Length == 1 ? flow.ExitPoints[0] : null;
    }

    private static bool SubsequentStatementsAreInTheSameBlock(
        TIfStatementSyntax ifNode,
        ImmutableArray<StatementRange> subsequentStatementRanges)
    {
        return subsequentStatementRanges.Length == 1 &&
               ifNode.Parent == subsequentStatementRanges[0].Parent;
    }

    private TSyntaxKind GetNearestParentJumpStatementKind(SyntaxNode ifNode)
    {
        foreach (var node in ifNode.Ancestors())
        {
            var jumpStatementRawKind = GetJumpStatementKind(node);
            if (jumpStatementRawKind != null)
                return jumpStatementRawKind.Value;
        }

        throw ExceptionUtilities.Unreachable();
    }

    private bool IsEmptyStatementRange(StatementRange statementRange)
    {
        if (!statementRange.IsEmpty)
        {
            var parent = statementRange.Parent;
            if (!IsStatementContainer(parent))
            {
                Debug.Assert(statementRange.FirstStatement == statementRange.LastStatement);
                return statementRange.FirstStatement.DescendantNodesAndSelf().All(IsNoOpSyntaxNode);
            }

            var statements = GetStatements(parent);
            var firstIndex = statements.IndexOf(statementRange.FirstStatement);
            var lastIndex = statements.IndexOf(statementRange.LastStatement);
            for (var i = firstIndex; i <= lastIndex; i++)
            {
                if (!statements[i].DescendantNodesAndSelf().All(IsNoOpSyntaxNode))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private ImmutableArray<StatementRange> GetSubsequentStatementRanges(TIfStatementSyntax ifNode)
    {
        using var _ = ArrayBuilder<StatementRange>.GetInstance(out var builder);

        TStatementSyntax innerStatement = ifNode;
        foreach (var node in ifNode.Ancestors())
        {
            var nextStatement = GetNextStatement(innerStatement);
            if (nextStatement != null && IsStatementContainer(node))
                builder.Add(new StatementRange(nextStatement, GetStatements(node).Last()));

            if (!CanControlFlowOut(node))
            {
                // We no longer need to continue since other statements
                // are out of reach, as far as this analysis concerned.
                break;
            }

            if (IsExecutableStatement(node))
                innerStatement = (TStatementSyntax)node;
        }

        return builder.ToImmutable();
    }

    private SyntaxNode GetRootWithInvertIfStatement(
        SourceText text,
        SyntaxNode root,
        TIfStatementSyntax ifNode,
        InvertIfStyle invertIfStyle,
        SyntaxNode? subsequentSingleExitPoint,
        SyntaxNode negatedExpression,
        ISyntaxFacts syntaxFacts)
    {
        switch (invertIfStyle)
        {
            case InvertIfStyle.IfWithElse_SwapIfBodyWithElseBody:
                {
                    var updatedIf = UpdateIf(
                        text,
                        ifNode: ifNode,
                        condition: negatedExpression,
                        trueStatement: GetElseBody(ifNode)!,
                        falseStatement: GetIfBody(ifNode));

                    return root.ReplaceNode(ifNode, updatedIf);
                }

            case InvertIfStyle.IfWithoutElse_MoveIfBodyToElseClause:
                {
                    var updatedIf = UpdateIf(
                        text,
                        ifNode: ifNode,
                        condition: negatedExpression,
                        trueStatement: GetEmptyEmbeddedStatement(),
                        falseStatement: GetIfBody(ifNode));

                    return root.ReplaceNode(ifNode, updatedIf);
                }

            case InvertIfStyle.IfWithoutElse_WithNegatedCondition:
                {
                    var updatedIf = UpdateIf(
                        text,
                        ifNode: ifNode,
                        condition: negatedExpression,
                        trueStatement: GetIfBody(ifNode));

                    return root.ReplaceNode(ifNode, updatedIf);
                }

            case InvertIfStyle.IfWithoutElse_SwapIfBodyWithSubsequentStatements:
                {
                    var currentParent = ifNode.GetRequiredParent();
                    var statements = GetStatements(currentParent);
                    var index = statements.IndexOf(ifNode);

                    var statementsBeforeIf = statements.Take(index);
                    var statementsAfterIf = statements.Skip(index + 1).ToImmutableArray();

                    var ifBody = GetIfBody(ifNode);

                    var newTrailing = UnwrapBlock(ifBody).ToArray();

                    if (newTrailing.Length > 0)
                    {
                        // Get leading and trailing space of the expressions to preserve for the user
                        // ex:
                        // if (true)
                        // {
                        //    return true;
                        // }
                        //              // <<< preserve this line
                        // // preserve this comment
                        // return false;
                        var leadingTrivia = GetLeadingSpace(statementsAfterIf[0].GetLeadingTrivia()).Concat(GetTriviaAfterSpace(newTrailing[0].GetLeadingTrivia()));
                        var trailingTrivia = GetTriviaUntilSpace(newTrailing[^1].GetTrailingTrivia()).Concat(GetTrailingSpace(statementsAfterIf[^1].GetTrailingTrivia()));
                        newTrailing[0] = newTrailing[0].WithLeadingTrivia(leadingTrivia);
                        newTrailing[^1] = newTrailing[^1].WithTrailingTrivia(trailingTrivia);
                    }

                    var updatedIf = UpdateIf(
                        text,
                        ifNode: ifNode,
                        condition: negatedExpression,
                        trueStatement: AsEmbeddedStatement(statementsAfterIf, original: ifBody));

                    var updatedParent = WithStatements(
                        currentParent,
                        statementsBeforeIf.Concat(updatedIf).Concat(newTrailing));

                    return root.ReplaceNode(currentParent, updatedParent.WithAdditionalAnnotations(Formatter.Annotation));
                }

            case InvertIfStyle.IfWithoutElse_WithNearmostJumpStatement:
                {
                    var currentParent = ifNode.GetRequiredParent();
                    var statements = GetStatements(currentParent);
                    var index = statements.IndexOf(ifNode);

                    var ifBody = GetIfBody(ifNode);
                    var newIfBody = GetJumpStatement(GetNearestParentJumpStatementKind(ifNode));

                    var updatedIf = UpdateIf(
                        text,
                        ifNode: ifNode,
                        condition: negatedExpression,
                        trueStatement: AsEmbeddedStatement([newIfBody], original: ifBody));

                    var statementsBeforeIf = statements.Take(index);

                    var updatedParent = WithStatements(
                        currentParent,
                        statementsBeforeIf.Concat(updatedIf).Concat(UnwrapBlock(ifBody)));

                    return root.ReplaceNode(currentParent, updatedParent.WithAdditionalAnnotations(Formatter.Annotation));
                }

            case InvertIfStyle.IfWithoutElse_WithSubsequentExitPointStatement:
                {
                    Debug.Assert(subsequentSingleExitPoint is TStatementSyntax);

                    var currentParent = ifNode.GetRequiredParent();
                    var statements = GetStatements(currentParent);
                    var index = statements.IndexOf(ifNode);

                    var ifBody = GetIfBody(ifNode);
                    var newIfBody = (TStatementSyntax)subsequentSingleExitPoint;

                    var updatedIf = UpdateIf(
                        text,
                        ifNode: ifNode,
                        condition: negatedExpression,
                        trueStatement: AsEmbeddedStatement([newIfBody], ifBody));

                    var statementsBeforeIf = statements.Take(index);

                    var updatedParent = WithStatements(
                        currentParent,
                        statementsBeforeIf.Concat(updatedIf).Concat(UnwrapBlock(ifBody)).Concat(newIfBody));

                    return root.ReplaceNode(currentParent, updatedParent.WithAdditionalAnnotations(Formatter.Annotation));
                }

            case InvertIfStyle.IfWithoutElse_MoveSubsequentStatementsToIfBody:
                {
                    var currentParent = ifNode.GetRequiredParent();
                    var statements = GetStatements(currentParent);
                    var index = statements.IndexOf(ifNode);

                    var statementsBeforeIf = statements.Take(index);
                    var statementsAfterIf = statements.Skip(index + 1);
                    var ifBody = GetIfBody(ifNode);

                    var updatedIf = UpdateIf(
                        text,
                        ifNode: ifNode,
                        condition: negatedExpression,
                        trueStatement: AsEmbeddedStatement(statementsAfterIf, ifBody));

                    var updatedParent = WithStatements(
                        currentParent,
                        statementsBeforeIf.Concat(updatedIf));

                    return root.ReplaceNode(currentParent, updatedParent.WithAdditionalAnnotations(Formatter.Annotation));
                }

            case InvertIfStyle.IfWithoutElse_WithElseClause:
                {
                    var currentParent = ifNode.GetRequiredParent();
                    var statements = GetStatements(currentParent);
                    var index = statements.IndexOf(ifNode);

                    var statementsBeforeIf = statements.Take(index);
                    var statementsAfterIf = statements.Skip(index + 1);

                    var ifBody = GetIfBody(ifNode);

                    var updatedIf = UpdateIf(
                        text,
                        ifNode: ifNode,
                        condition: negatedExpression,
                        trueStatement: AsEmbeddedStatement(statementsAfterIf, ifBody),
                        falseStatement: ifBody);

                    var updatedParent = WithStatements(
                        currentParent,
                        statementsBeforeIf.Concat(updatedIf));

                    return root.ReplaceNode(currentParent, updatedParent.WithAdditionalAnnotations(Formatter.Annotation));
                }

            default:
                throw ExceptionUtilities.UnexpectedValue(invertIfStyle);
        }

        // 
        // local functions
        //
        IEnumerable<SyntaxTrivia> GetTriviaAfterSpace(IEnumerable<SyntaxTrivia> syntaxTrivias)
        {
            return syntaxTrivias.SkipWhile(syntaxFacts.IsWhitespaceOrEndOfLineTrivia);
        }

        IEnumerable<SyntaxTrivia> GetTriviaUntilSpace(IEnumerable<SyntaxTrivia> syntaxTrivias)
        {
            return GetTriviaAfterSpace(syntaxTrivias.Reverse()).Reverse();
        }

        IEnumerable<SyntaxTrivia> GetTrailingSpace(IEnumerable<SyntaxTrivia> syntaxTrivias)
        {
            return GetLeadingSpace(syntaxTrivias.Reverse()).Reverse();
        }

        IEnumerable<SyntaxTrivia> GetLeadingSpace(IEnumerable<SyntaxTrivia> syntaxTrivias)
        {
            return syntaxTrivias.TakeWhile(syntaxFacts.IsWhitespaceOrEndOfLineTrivia);
        }
    }
}
