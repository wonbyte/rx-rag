using Microsoft.Extensions.AI;
using RxRag.Core.Answering;

namespace RxRag.Evals;

// One test case from golden.json.
internal sealed record EvalCase(string Id, string Question, string? ExpectGeneric, bool ExpectNoAnswer);

// Scores from the judge model. 1 = bad, 5 = good.
internal sealed record JudgeScore(int Grounded, int Relevant, string Reason);

// "LLM as judge": a model grades the answer against a written rubric.
//
// Known weakness: here the judge is the SAME model that wrote the answer,
// and models tend to grade themselves kindly. In production, use a different
// (often stronger) judge model, and check the judge against human grades
// on a sample from time to time.
internal sealed class Judge(IChatClient chat)
{
    public async Task<JudgeScore> ScoreAsync(string question, Answer answer, CancellationToken ct)
    {
        var excerpts = string.Join("\n\n", answer.Citations.Select(c => $"[{c.Number}] {c.Source}\n{c.Excerpt}"));

        var prompt = $"""
            You grade answers from a drug-label assistant. Be strict.

            Question:
            {question}

            Cited excerpts (the ONLY allowed evidence):
            {excerpts}

            Answer:
            {answer.Text}

            Score two things from 1 to 5:
            - grounded: 5 = every claim in the answer is supported by the cited excerpts. 1 = claims are missing from or contradict the excerpts.
            - relevant: 5 = the answer directly answers the question. 1 = it does not.
            Give a one-sentence reason.
            """;

        // Structured output: the model must return JSON in the JudgeScore
        // shape, and the library parses it. No fragile text parsing.
        var response = await chat.GetResponseAsync<JudgeScore>(prompt, cancellationToken: ct);
        return response.Result;
    }
}