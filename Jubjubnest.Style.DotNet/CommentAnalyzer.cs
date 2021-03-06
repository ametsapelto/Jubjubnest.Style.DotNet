using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Text.RegularExpressions;

namespace Jubjubnest.Style.DotNet
{
	/// <summary>
	/// Analyzes the commenting style.
	/// </summary>
	[ DiagnosticAnalyzer( LanguageNames.CSharp ) ]
	public class CommentAnalyzer : DiagnosticAnalyzer
	{
		/// <summary>All code segments must be commented.</summary>
		public static RuleDescription CommentedSegments { get; } =
				new RuleDescription( "CommentedSegments", "Comments" );

		/// <summary>Comments must have a newline before them.</summary>
		public static RuleDescription NewlineBeforeComment { get; } =
				new RuleDescription( "NewlineBeforeComment", "Comments" );

		/// <summary>There must be two spaces separating a trailing comment from the code before it.</summary>
		public static RuleDescription SpacesBeforeTrailingComment { get; } =
				new RuleDescription( "SpacesBeforeTrailingComment", "Comments" );

		/// <summary>Comments must have a space after the '//'.</summary>
		public static RuleDescription CommentStartsWithSpace { get; } =
				new RuleDescription( "CommentStartsWithSpace", "Comments" );

		/// <summary>
		/// Statements that may be alone as the last statement in a function without requiring a comment.
		///
		/// Often these are the only statement in the function.
		/// </summary>
		public static HashSet< SyntaxKind > SoloStatementsWithoutComments { get; } =
				new HashSet< SyntaxKind >
				{
					SyntaxKind.ReturnStatement,
					SyntaxKind.AddAssignmentExpression,
					SyntaxKind.SubtractAssignmentExpression,
					SyntaxKind.SimpleAssignmentExpression,
					SyntaxKind.InvocationExpression,
					SyntaxKind.ThrowStatement,
				};

		/// <summary>
		/// Supported diagnostic rules.
		/// </summary>
		public override ImmutableArray< DiagnosticDescriptor > SupportedDiagnostics =>
				ImmutableArray.Create(
					CommentedSegments.Rule,
					NewlineBeforeComment.Rule,
					SpacesBeforeTrailingComment.Rule,
					CommentStartsWithSpace.Rule );

		/// <summary>
		/// Initialize the analyzer.
		/// </summary>
		/// <param name="context">Analysis context the analysis actions are registered on.</param>
		public override void Initialize( AnalysisContext context )
		{
			// Ignore generated files.
			context.ConfigureGeneratedCodeAnalysis( GeneratedCodeAnalysisFlags.None );

			// Register the actions.
			context.RegisterSyntaxNodeAction( AnalyzeCodeBlocks, SyntaxKind.Block );
			context.RegisterSyntaxTreeAction( AnalyzeAllComments );
		}

		/// <summary>
		/// Regex validating there is a space after comments.
		///
		/// Handles both normal comments and XML documentation comments.
		/// </summary>
		private static readonly Regex SPACE_AFTER_COMMENT_SLASHES_REGEX = new Regex( "^///?(?: |$)" );

		/// <summary>
		/// Analyze the comments.
		/// </summary>
		/// <param name="context">Analysis context.</param>
		private static void AnalyzeAllComments( SyntaxTreeAnalysisContext context )
		{
			// Get all comments.
			var root = context.Tree.GetRoot( context.CancellationToken );
			var comments = root.DescendantTrivia().Where( t => t.IsKind( SyntaxKind.SingleLineCommentTrivia ) );

			// Analyze each comment separately.
			foreach( var comment in comments )
			{
				// If the comment doesn't start with double '//' we have no idea what it is.
				var str = comment.ToString();
				if( !str.StartsWith( "//", StringComparison.Ordinal ) )
					continue;

				// If the comment has space after slashes it's okay.
				if( SPACE_AFTER_COMMENT_SLASHES_REGEX.IsMatch( str ) )
					continue;

				// Create the diagnostic message and report it.
				var diagnostic = Diagnostic.Create(
						CommentStartsWithSpace.Rule,
						comment.GetLocation() );
				context.ReportDiagnostic( diagnostic );
			}
		}

		/// <summary>
		/// Analyze the code segments within code blocks.
		/// </summary>
		/// <param name="context">Analysis context.</param>
		private static void AnalyzeCodeBlocks( SyntaxNodeAnalysisContext context )
		{
			// Get the block.
			var block = (BlockSyntax)context.Node;
			var blockStart = block.OpenBraceToken.GetLocation().GetLineSpan().StartLinePosition;
			var blockEnd = block.CloseBraceToken.GetLocation().GetLineSpan().EndLinePosition;

			// If the block is a single-line block, ignore comment requirements.
			if( blockStart.Line == blockEnd.Line )
				return;

			// Variables used during iteration.
			int previousStatementEndLine = int.MinValue;
			int segmentStart = -1;
			int nodesInSegment = 0;
			SyntaxNode firstInSegment = null;
			SyntaxNode lastInSegment = null;

			// Process all top-level code nodes within the block.
			foreach( var childNode in context.Node.ChildNodes() )
			{
				// Check for the leading and trailing comment spacing.
				CheckLeadingCommentSpace( context, childNode );
				CheckTrailingCommentSpace( context, childNode );

				// Store the segment start if we're not tracking a segment currently.
				var lineSpan = childNode.GetLocation().GetLineSpan();
				if( segmentStart == -1 )
				{
					// This is the first segment. Initialize the tracking variables.
					segmentStart = lineSpan.StartLinePosition.Line;
					previousStatementEndLine = lineSpan.EndLinePosition.Line;
					firstInSegment = childNode;
					lastInSegment = childNode;
				}

				// Check whether the current syntax node is attached to the previous node.
				if( lineSpan.StartLinePosition.Line <= previousStatementEndLine + 1 )
				{
					// The nodes are attached. Find the next one.
					previousStatementEndLine = lineSpan.EndLinePosition.Line;
					lastInSegment = childNode;
					nodesInSegment += 1;
					continue;
				}

				// Segment ended so we know its full length. Make sure it has a comment.
				RequireComment( context, firstInSegment, lastInSegment );

				// Start a new segment.
				segmentStart = lineSpan.StartLinePosition.Line;
				previousStatementEndLine = lineSpan.EndLinePosition.Line;
				nodesInSegment = 1;
				firstInSegment = childNode;
				lastInSegment = childNode;
			}

			// No more statements. We might need to process the last statmeent unless the
			// whole block was empty.
			if( firstInSegment != null )
			{
				// Last segment exists.

				// Get the statement kind.
				var kind = firstInSegment.IsKind( SyntaxKind.ExpressionStatement )
						? ( (ExpressionStatementSyntax)firstInSegment ).Expression.Kind()
						: firstInSegment.Kind();

				// Make an exception of a single statement if it's of an allowed kind.
				if( firstInSegment == lastInSegment &&
					SoloStatementsWithoutComments.Contains( kind ) )
				{
					// Single allowed statement ending the block.
					// Make an exception for the comment requirement.
				}
				else
				{
					// Check for comment as usual.
					RequireComment( context, firstInSegment, lastInSegment );
				}
			}
		}

		/// <summary>
		/// Check spacing rules in the trailing comments.
		/// </summary>
		/// <param name="context">Analysis context.</param>
		/// <param name="childNode">Node to check for trailing comments.</param>
		private static void CheckTrailingCommentSpace(
			SyntaxNodeAnalysisContext context,
			SyntaxNode childNode )
		{
			// If there are no trailing trivia, skip the whole node.
			if( !childNode.HasTrailingTrivia )
				return;

			// Gather all the continuous whitespace in the trivia.
			var trivia = childNode.GetTrailingTrivia().ToList();
			var cursor = 0;
			var whitespace = "";
			while( cursor < trivia.Count && trivia[ cursor ].IsKind( SyntaxKind.WhitespaceTrivia ) )
				whitespace += trivia[ cursor++ ].ToString();

			// Check whether we have trailing comment trivia after the whitespace.
			if( cursor >= trivia.Count ||
				!trivia[ cursor ].IsKind( SyntaxKind.SingleLineCommentTrivia ) )
			{
				// No trailing trivia. Stop processing the node.
				return;
			}

			// If there is exactly two characters of whitespace before the comment, we're
			// okay and there's no need for warning.
			if( SyntaxHelper.GetTextLength( whitespace ) == 2 )
				return;

			// There's comments and they are not separated by two spaces.
			// Report the diagnostic.
			var diagnostic = Diagnostic.Create(
					SpacesBeforeTrailingComment.Rule,
					trivia[ cursor ].GetLocation() );
			context.ReportDiagnostic( diagnostic );
		}

		/// <summary>
		/// Check spacing rules in the leading comments.
		/// </summary>
		/// <param name="context">Analysis context.</param>
		/// <param name="childNode">Node to check for leading comments.</param>
		private static void CheckLeadingCommentSpace(
			SyntaxNodeAnalysisContext context,
			SyntaxNode childNode )
		{
			// If there's no leading trivia we can skip the whole node.
			if( !childNode.HasLeadingTrivia )
				return;

			// Get the leading comments.
			var comments = childNode.GetLeadingTrivia()
								.Where( trivia => trivia.IsKind( SyntaxKind.SingleLineCommentTrivia ) )
								.ToList();

			// We'll cache the source text if we need it.
			SourceText sourceText = null;

			// Process each comment trivia.
			//
			// Keep track of the previous comment line since we will consider subsequent lines
			// as single comments and won't require empty lines between them.
			var previousLineNumber = int.MinValue;
			for( var i = 0; i < comments.Count; ++i )
			{
				// Grab the comment line number.
				var comment = comments[ i ];
				var currentLineNumber = comment.GetLocation().GetLineSpan().StartLinePosition.Line;

				// If this is continuation block, skip the checks.
				if( previousLineNumber == currentLineNumber - 1 )
				{
					// Continuation comment. Store the line number and proceed to the next comment.
					previousLineNumber = currentLineNumber;
					continue;
				}

				// Not a continuation comment. Require empty line before.

				// Store the previous line number as we won't need it during this iteration
				// anymore and we might continue out of it at some point.
				previousLineNumber = currentLineNumber;

				// If we haven't retrieved the source text yet, do so now.
				// The source should be the same for all the trivia here.
				if( sourceText == null )
					sourceText = comment.GetLocation().SourceTree
										.GetText( context.CancellationToken );

				// Get the text for the line above.
				var lineAbove = sourceText.GetSubText( sourceText.Lines[ currentLineNumber - 1 ].Span )
										.ToString().Trim();

				// If the previous line is nothing but an opening brace, consider this okay.
				if( string.IsNullOrEmpty( lineAbove ) || lineAbove == "{" )
					continue;

				// Create the diagnostic message and report it.
				var diagnostic = Diagnostic.Create(
						NewlineBeforeComment.Rule,
						comment.GetLocation() );
				context.ReportDiagnostic( diagnostic );
			}
		}

		/// <summary>
		/// Process the code segment and ensure it has leading comment.
		/// </summary>
		/// <param name="context">Analysis context.</param>
		/// <param name="firstInSegment">First node in the code segment.</param>
		/// <param name="lastInSegment">Last node in the code segment.</param>
		private static void RequireComment(
			SyntaxNodeAnalysisContext context,
			SyntaxNode firstInSegment,
			SyntaxNode lastInSegment )
		{
			// Try to find a leading comment for the segment.
			var hasComments = false;
			if( firstInSegment.HasLeadingTrivia )
			{
				// Get the list of all trivia. This includes comments, end of lines, whitespace, etc.
				var trivia = firstInSegment.GetLeadingTrivia().ToList();

				// Find the last comment. If one exists, calculate the amount of new lines afterwards.
				var newLines = int.MaxValue;
				var lastComment = trivia.FindLastIndex( item => item.IsKind( SyntaxKind.SingleLineCommentTrivia ) );
				if( lastComment >= 0 )
					newLines = trivia.Skip( lastComment ).Count( item => item.IsKind( SyntaxKind.EndOfLineTrivia ) );

				// If new line was within 1 line break (no empty line between),
				// consider it being the comment of the current segment.
				if( newLines <= 1 )
					hasComments = true;
			}

			// If the segment has no comment, flag it for error.
			if( !hasComments )
			{
				// Create the diagnostic message and report it.
				var diagnostic = Diagnostic.Create(
						CommentedSegments.Rule,
						Location.Create(
							context.Node.GetLocation().SourceTree,
							TextSpan.FromBounds( firstInSegment.Span.Start, lastInSegment.Span.End ) ) );
				context.ReportDiagnostic( diagnostic );
			}
		}
	}
}
