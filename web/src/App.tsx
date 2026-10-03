import { useRef, useState, type FormEvent, type ReactNode } from 'react'
import { streamAnswer, type Citation } from './api'
import './App.css'

// Must match PromptBuilder.NoAnswer in the API.
const REFUSAL = "I can't answer that from the drug labels I have"

// Questions the default ingest set can answer. They also show new users
// what kind of question works.
const EXAMPLES = [
  'Does ibuprofen have a stomach bleeding warning?',
  'What is famotidine used for?',
  'Can diphenhydramine make me drowsy?',
]

export default function App() {
  const [question, setQuestion] = useState('')
  const [asked, setAsked] = useState('')
  const [answer, setAnswer] = useState('')
  const [citations, setCitations] = useState<Citation[]>([])
  const [openSource, setOpenSource] = useState<number | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Lets the user stop a long answer. Aborting closes the HTTP stream,
  // and the server then cancels the model call.
  const abortRef = useRef<AbortController | null>(null)

  async function ask(text: string) {
    const q = text.trim()
    if (!q) return

    abortRef.current?.abort()
    const controller = new AbortController()
    abortRef.current = controller

    setAsked(q)
    setBusy(true)
    setError(null)
    setAnswer('')
    setCitations([])
    setOpenSource(null)

    try {
      const cites = await streamAnswer(q, (t) => setAnswer((a) => a + t), controller.signal)
      setCitations(cites)
    } catch (err) {
      if (!controller.signal.aborted) {
        setError(err instanceof Error ? err.message : 'The request failed. Try again.')
      }
    } finally {
      setBusy(false)
    }
  }

  function onSubmit(e: FormEvent) {
    e.preventDefault()
    void ask(question)
  }

  function onExample(text: string) {
    setQuestion(text)
    void ask(text)
  }

  // A citation number in the answer opens its source and scrolls to it.
  function showSource(n: number) {
    setOpenSource(n)
    document.getElementById(`source-${n}`)?.scrollIntoView({ behavior: 'smooth', block: 'nearest' })
  }

  const refused = !busy && answer.startsWith(REFUSAL)
  const showExamples = !asked && !busy

  return (
    <main className="app">
      <h1 className="title">Ask about a medicine.</h1>
      <p className="intro">
        Answers come only from FDA drug labels, with the source for each fact. This is not medical
        advice. Check with your pharmacist or doctor.
      </p>

      <form className="ask" onSubmit={onSubmit}>
        <input
          value={question}
          onChange={(e) => setQuestion(e.target.value)}
          maxLength={500}
          placeholder="Can I take ibuprofen with warfarin?"
          disabled={busy}
          aria-label="Your question"
          autoFocus
        />
        {busy ? (
          <button type="button" className="button button-quiet" onClick={() => abortRef.current?.abort()}>
            Stop
          </button>
        ) : (
          <button type="submit" className="button button-primary" disabled={!question.trim()}>
            Ask
          </button>
        )}
      </form>

      {showExamples && (
        <div className="examples">
          <p>Try one of these:</p>
          <ul>
            {EXAMPLES.map((ex) => (
              <li key={ex}>
                <button type="button" className="example" onClick={() => onExample(ex)}>
                  {ex}
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}

      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}

      {busy && !answer && (
        <p className="status" aria-live="polite">
          Reading the labels...
        </p>
      )}

      {answer && (
        <section className={refused ? 'label label-refused' : 'label'} aria-live="polite">
          <p className="label-text">
            {withCitations(answer, citations, showSource)}
            {busy && <span className="caret" aria-hidden="true" />}
          </p>
          {refused && (
            <p className="hint">
              Try naming the drug and what you want to know, like a warning, a use, or a side
              effect.
            </p>
          )}
        </section>
      )}

      {citations.length > 0 && (
        <section className="sources">
          <h2>Sources</h2>
          <ol>
            {citations.map((c) => (
              <li
                key={c.chunkId}
                id={`source-${c.number}`}
                className={openSource === c.number ? 'source source-active' : 'source'}
              >
                <span className="source-number" aria-hidden="true">
                  {c.number}
                </span>
                <details
                  open={openSource === c.number}
                  onToggle={(e) => {
                    // Keep React state in sync when the user opens or
                    // closes a source by clicking it directly.
                    const isOpen = e.currentTarget.open
                    setOpenSource((cur) => (isOpen ? c.number : cur === c.number ? null : cur))
                  }}
                >
                  <summary>
                    <span className="visually-hidden">Source {c.number}: </span>
                    {c.source}
                  </summary>
                  <p className="excerpt">{c.excerpt}</p>
                </details>
              </li>
            ))}
          </ol>
        </section>
      )}

      <p className="footnote">Label text from openFDA. Labels can be out of date.</p>
    </main>
  )
}

// Turns "[2]" markers in the answer into clickable citation numbers.
// While the answer streams, citations are not known yet, so markers show
// as plain numbers; they become buttons once the "done" event arrives.
function withCitations(text: string, citations: Citation[], onCite: (n: number) => void): ReactNode[] {
  const known = new Set(citations.map((c) => c.number))

  // split() with a capture group keeps the markers in the result array.
  return text.split(/(\[\d{1,2}\])/g).map((part, i) => {
    const match = /^\[(\d{1,2})\]$/.exec(part)
    if (!match) return part

    const n = Number(match[1])
    if (!known.has(n)) {
      return (
        <span key={i} className="cite" aria-hidden="true">
          {n}
        </span>
      )
    }

    return (
      <button key={i} type="button" className="cite" onClick={() => onCite(n)} aria-label={`Show source ${n}`}>
        {n}
      </button>
    )
  })
}
