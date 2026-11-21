// Worker entry module. It exports only the fetch handler.
// index.js also exports helpers for the tests; workerd rejects non-handler
// named exports (such as the VERSION string) on the entry module.
import { handle } from "./index.js";

export default { fetch: (request, env, ctx) => handle(request, env, ctx) };
