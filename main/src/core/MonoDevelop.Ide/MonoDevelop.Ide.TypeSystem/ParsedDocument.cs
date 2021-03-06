// 
// ParsedDocument.cs
//  
// Author:
//       Mike Krüger <mkrueger@novell.com>
// 
// Copyright (c) 2011 Mike Krüger <mkrueger@novell.com>
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using ICSharpCode.NRefactory.TypeSystem;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Threading;
using MonoDevelop.Ide.Editor;
using System.Threading.Tasks;
using MonoDevelop.Core.Text;

namespace MonoDevelop.Ide.TypeSystem
{
	[Flags]
	[Obsolete ("Not supported in new editor")]
	public enum ParsedDocumentFlags
	{
		None            = 0,
		NonSerializable = 1,

		/// <summary>
		/// Used for files where a custom folding extension is taken.
		/// </summary>
		SkipFoldings   = 2,

		/// <summary>
		/// Used for files that have a custom completion extension.
		/// </summary>
		HasCustomCompletionExtension = 4
	}

	[Obsolete ("Not supported in new editor")]
	public abstract class ParsedDocument
	{
		DateTime lastWriteTimeUtc = DateTime.UtcNow;
		public DateTime LastWriteTimeUtc {
			get { return lastWriteTimeUtc; }
			set { lastWriteTimeUtc = value; }
		}


		[NonSerialized]
		ParsedDocumentFlags flags;
		public ParsedDocumentFlags Flags {
			get {
				return flags;
			}
			set {
				flags = value;
			}
		}

		string fileName;
		public virtual string FileName {
			get {
				return fileName;
			}
			protected set {
				fileName = value;
			}
		}

		/// <summary>
		/// Gets or sets a value indicating whether this instance is invalid and needs to be reparsed.
		/// </summary>
		public bool IsInvalid {
			get;
			set;
		}

		public abstract Task<IReadOnlyList<Comment>> GetCommentsAsync (CancellationToken cancellationToken = default(CancellationToken));
		public abstract Task<IReadOnlyList<Tag>> GetTagCommentsAsync (CancellationToken cancellationToken = default(CancellationToken));
		public abstract Task<IReadOnlyList<FoldingRegion>> GetFoldingsAsync (CancellationToken cancellationToken = default(CancellationToken));

		public async Task<IEnumerable<FoldingRegion>> GetUserRegionsAsync (CancellationToken cancellationToken = default(CancellationToken))
		{
			var foldings = await GetFoldingsAsync (cancellationToken).ConfigureAwait (false);
			return foldings.Where (f => f.Type == FoldType.UserRegion);
		}

		public abstract Task<IReadOnlyList<Error>> GetErrorsAsync (CancellationToken cancellationToken = default(CancellationToken));

		public async Task<bool> HasErrorsAsync (CancellationToken cancellationToken = default (CancellationToken))
		{
			return (await GetErrorsAsync (cancellationToken).ConfigureAwait (false)).Any (e => e.ErrorType == ErrorType.Error);
		}
		
		/// <summary>
		/// Gets or sets the language ast used by specific language backends.
		/// </summary>
		public object Ast {
			get;
			set;
		}
		
		public ParsedDocument ()
		{
		}
		
		public ParsedDocument (string fileName)
		{
			this.fileName = fileName;
		}
	}

	[Obsolete]
	public class DefaultParsedDocument : ParsedDocument
	{
		public DefaultParsedDocument (string fileName) : base (fileName)
		{
			Flags |= ParsedDocumentFlags.NonSerializable;
		}

		List<Comment> comments = new List<Comment> ();

		public void Add (Comment comment)
		{
			comments.Add (comment);
		}

		public void AddRange (IEnumerable<Comment> comments)
		{
			this.comments.AddRange (comments);
		}

		public override Task<IReadOnlyList<Comment>> GetCommentsAsync (CancellationToken cancellationToken = default(CancellationToken))
		{
			return Task.FromResult<IReadOnlyList<Comment>> (comments);
		}

		List<Tag> tagComments = new List<Tag> ();

		public void Add (Tag tagComment)
		{
			tagComments.Add (tagComment);
		}

		public void AddRange (IEnumerable<Tag> tagComments)
		{
			this.tagComments.AddRange (tagComments);
		}

		public override Task<IReadOnlyList<Tag>> GetTagCommentsAsync (CancellationToken cancellationToken = default(CancellationToken))
		{
			return Task.FromResult<IReadOnlyList<Tag>> (tagComments);
		}

		List<FoldingRegion> foldingRegions = new List<FoldingRegion> ();

		public void Add (FoldingRegion foldingRegion)
		{
			foldingRegions.Add (foldingRegion);
		}

		public void AddRange (IEnumerable<FoldingRegion> foldingRegions)
		{
			this.foldingRegions.AddRange (foldingRegions);
		}

		public override Task<IReadOnlyList<FoldingRegion>> GetFoldingsAsync (CancellationToken cancellationToken = default(CancellationToken))
		{
			return Task.FromResult<IReadOnlyList<FoldingRegion>> (foldingRegions);
		}

		List<Error> errors = new List<Error> ();

		public void Add (Error error)
		{
			errors.Add (error);
		}

		public void AddRange (IEnumerable<Error> errors)
		{
			this.errors.AddRange (errors);
		}

		public override Task<IReadOnlyList<Error>> GetErrorsAsync (CancellationToken cancellationToken = default(CancellationToken))
		{
			return Task.FromResult<IReadOnlyList<Error>> (errors);
		}
	}

	[Obsolete ("Old editor")]
	public static class FoldingUtilities
	{
		static bool IncompleteOrSingleLine (DomRegion region)
		{
			return region.BeginLine <= 0 || region.EndLine <= region.BeginLine;
		}

		[Obsolete("Language backends need to implement a custom version.")]
		public static IEnumerable<FoldingRegion> ToFolds (this IReadOnlyList<Comment> comments)
		{
			for (int i = 0; i < comments.Count; i++) {
				Comment comment = comments [i];
				
				if (comment.CommentType == CommentType.Block) {
					int startOffset = 0;
					if (comment.Region.BeginLine == comment.Region.EndLine)
						continue;
					while (startOffset < comment.Text.Length) {
						char ch = comment.Text [startOffset];
						if (!char.IsWhiteSpace (ch) && ch != '*')
							break;
						startOffset++;
					}
					int endOffset = startOffset;
					while (endOffset < comment.Text.Length) {
						char ch = comment.Text [endOffset];
						if (ch == '\r' || ch == '\n' || ch == '*')
							break;
						endOffset++;
					}
					
					string txt;
					if (endOffset > startOffset) {
						txt = "/* " + GetFirstLine (comment.Text) + " ...";
					} else {
						txt = "/* */";
					}
					yield return new FoldingRegion (txt, comment.Region, FoldType.Comment);
					continue;
				}
				
				if (!comment.CommentStartsLine)
					continue;
				int j = i;
				int curLine = comment.Region.BeginLine - 1;
				var end = comment.Region.End;
				var commentText = new StringBuilder ();
				for (; j < comments.Count; j++) {
					Comment curComment = comments [j];
					if (curComment == null || !curComment.CommentStartsLine 
					    || curComment.CommentType != comment.CommentType 
					    || curLine + 1 != curComment.Region.BeginLine)
						break;
					commentText.Append (curComment.Text);
					end = curComment.Region.End;
					curLine = curComment.Region.BeginLine;
				}
				
				if (j - i > 1 || (comment.IsDocumentation && comment.Region.BeginLine < comment.Region.EndLine)) {
					string txt = null;
					if (comment.IsDocumentation) {
						string cmtText = commentText.ToString ();
						int idx = cmtText.IndexOf ("<summary>", StringComparison.Ordinal);
						if (idx >= 0) {
							int maxOffset = cmtText.IndexOf ("</summary>", StringComparison.Ordinal);
							while (maxOffset > 0 && cmtText[maxOffset-1] == ' ')
								maxOffset--;
							if (maxOffset < 0)
								maxOffset = cmtText.Length;
							int startOffset = idx + "<summary>".Length;
							while (startOffset < maxOffset) {
								char ch = cmtText [startOffset];
								if (!char.IsWhiteSpace (ch) && ch != '/')
									break;
								startOffset++;
							}
							int endOffset = startOffset;
							while (endOffset < maxOffset) {
								char ch = cmtText [endOffset];
								if (ch == '\r' || ch == '\n')
									break;
								endOffset++;
							}
							if (endOffset > startOffset)
								txt = "/// <summary> " + cmtText.Substring (startOffset, endOffset - startOffset).Trim()+ " ...";
						}
						if (txt == null)
							txt = "/// " + comment.Text.Trim () + " ...";
					} else {
						txt = "// " + comment.Text.Trim () + " ...";
					}
					
					yield return new FoldingRegion (txt,
						new DocumentRegion (comment.Region.Begin, end),
						FoldType.Comment);
					i = j - 1;
				}
			}
		}

		static string GetFirstLine (string text)
		{
			int start = 0;
			while (start < text.Length) {
				char ch = text [start];
				if (ch != ' ' && ch != '\t')
					break;
				start++;
			}
			int end = start;

			while (end < text.Length) {
				char ch = text [end];
				if (NewLine.IsNewLine (ch))
					break;
				end++;
			}
			if (end <= start)
				return "";
			return text.Substring (start, end - start);
		}
		
		public static IEnumerable<FoldingRegion> FlagIfInsideMembers (this IEnumerable<FoldingRegion> folds,
			IEnumerable<IUnresolvedTypeDefinition> types, Action<FoldingRegion> flagAction)
		{
			foreach (FoldingRegion fold in folds) {
				foreach (var type in types) {
					if (fold.Region.IsInsideMember (type)) {
						flagAction (fold);
						break;
					}
				}
				yield return fold;
			}
		}
		
		static bool IsInsideMember (this DomRegion region, IUnresolvedTypeDefinition cl)
		{
			if (region.IsEmpty || cl == null || !cl.BodyRegion.IsInside (region.Begin))
				return false;
			foreach (var member in cl.Members) {
				if (member.BodyRegion.IsEmpty)
					continue;
				if (member.BodyRegion.IsInside (region.Begin) && member.BodyRegion.IsInside (region.End)) 
					return true;
			}
			foreach (var inner in cl.NestedTypes) {
				if (region.IsInsideMember (inner))
					return true;
			}
			return false;
		}

		static bool IsInsideMember (this DocumentRegion region, IUnresolvedTypeDefinition cl)
		{
			if (region.IsEmpty || cl == null || !cl.BodyRegion.IsInside (region.Begin.Line, region.Begin.Column))
				return false;
			foreach (var member in cl.Members) {
				if (member.BodyRegion.IsEmpty)
					continue;
				if (member.BodyRegion.IsInside (region.Begin.Line, region.Begin.Column) && member.BodyRegion.IsInside (region.End.Line, region.End.Column)) 
					return true;
			}
			foreach (var inner in cl.NestedTypes) {
				if (region.IsInsideMember (inner))
					return true;
			}
			return false;
		}
		
	}
}

