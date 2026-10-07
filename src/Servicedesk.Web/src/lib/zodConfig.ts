import { config } from "zod";

// Zod v4 probes `new Function("")` the first time it parses an object schema,
// to decide whether it may JIT-compile a fast path. Our CSP (no
// 'unsafe-eval') correctly blocks that probe — but every blocked probe files a
// `script-src eval` csp_violation, which made up ~95% of the audit log's CSP
// rows and drowned the real attack signal. jitless skips the probe entirely;
// the interpreted path is plenty fast for form-sized payloads.
//
// Imported first in main.tsx so it runs before any schema is parsed.
config({ jitless: true });
