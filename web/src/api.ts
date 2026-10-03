// Types match the C# records. ASP.NET Core writes JSON in camelCase.
export type Citation = {
  number: number
  source: string
  genericName: string
  chunkId: string
  excerpt: string
}

type AnswerEvent = {
  type: 'delta' | 'done'
  text?: string | null
  citations?: Citation[] | null
}

// We use fetch, not EventSource: EventSource can only send GET, and we
// POST the question in the body (questions do not belong in URLs or logs).
// So we read the SSE format by hand: events end with a blank line, and
// each "data:" line holds JSON.
export async function streamAnswer(
  question: string,
  onDelta: (text: string) => void,
  signal: AbortSignal,
): Promise<Citation[]> {
  const res = await fetch('/api/ask/stream', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ question }),
    signal,
  })

  if (!res.ok || !res.body) {
    throw new Error(await errorMessage(res))
  }

  const reader = res.body.pipeThrough(new TextDecoderStream()).getReader()
  let buffer = ''
  let citations: Citation[] = []

  // The server sends "done" last. If the stream ends without it, the
  // server failed after it already sent "200 OK" (for example a rate
  // limit from Azure). Without this flag, the user would see a half
  // answer and no error.
  let gotDone = false

  for (;;) {
    const { value, done } = await reader.read()
    if (done) break
    buffer += value

    let end: number
    while ((end = buffer.indexOf('\n\n')) !== -1) {
      const raw = buffer.slice(0, end)
      buffer = buffer.slice(end + 2)

      const data = raw
        .split('\n')
        .filter((line) => line.startsWith('data:'))
        .map((line) => line.slice(5).trimStart())
        .join('\n')
      if (!data) continue

      const evt = JSON.parse(data) as AnswerEvent
      if (evt.type === 'delta' && evt.text) onDelta(evt.text)
      if (evt.type === 'done') {
        citations = evt.citations ?? []
        gotDone = true
      }
    }
  }

  if (!gotDone) {
    throw new Error('The answer stopped before it finished. Try again.')
  }

  return citations
}

async function errorMessage(res: Response): Promise<string> {
  if (res.status === 429) return 'Too many questions. Wait a minute and try again.'
  try {
    const body = await res.json()
    const first = body?.errors && Object.values(body.errors)[0]
    return (Array.isArray(first) ? first[0] : body?.title) ?? `Error ${res.status}`
  } catch {
    return `Error ${res.status}`
  }
}
