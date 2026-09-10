using System.Collections.Generic;
using System.Linq;
using TensorSharp.Runtime.Speculative;
using Xunit;

namespace InferenceWeb.Tests;

/// <summary>An n-gram drafter seeded with a prompt that holds a passage twice must
/// propose the passage when the model starts quoting it - the shape of every
/// "repeat the text above" turn.</summary>
public class NGramQuoteProposalTests
{
    [Fact]
    public void QuotingAPassageFromThePromptIsDrafted()
    {
        int[] passage = Enumerable.Range(1000, 60).ToArray();          // distinct tokens
        int[] summaryAsk = Enumerable.Range(5000, 10).ToArray();
        int[] followUp = Enumerable.Range(6000, 8).ToArray();
        var prompt = passage.Concat(passage).Concat(summaryAsk).Concat(followUp).ToArray();

        var ngram = new NGramSpeculator(maxDraftTokens: 3);
        ngram.Commit(prompt, null, 0);                                  // SeedCommitted

        int position = prompt.Length;
        int drafted = 0, proposals = 0;
        var draft = new List<int>();
        // The model quotes the passage token by token; each step drafts from the
        // token it is about to forward, then commits it.
        for (int i = 0; i < passage.Length - 1; i++)
        {
            int lastToken = passage[i];
            draft.Clear();
            int n = ngram.Propose(new DraftContext { LastToken = lastToken, Position = position, MaxTokens = 3 }, draft);
            if (n > 0)
            {
                proposals++;
                drafted += n;
                // The drafts are the passage's continuation (past its end the corpus
                // continues into the second copy, which is also right).
                int inside = System.Math.Min(n, passage.Length - 1 - i);
                Assert.Equal(passage.Skip(i + 1).Take(inside), draft.Take(inside));
            }
            ngram.Commit(new[] { lastToken }, null, position);
            position++;
        }
        Assert.True(proposals > passage.Length / 2, $"only {proposals} proposals over {passage.Length - 1} quoted tokens");
        Assert.True(drafted > passage.Length, $"only {drafted} tokens drafted");
    }
}
