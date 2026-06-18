**Deep explain example** (`explain --depth 3 --max-methods 12`)

A real deep run: CodeTracer walks the call chain from `Agent.RunAsync` down 3 levels — **12
methods** (`L0` → `L1` → `L2`), each explained on its own — then writes an **end-to-end
synthesis** (`## End-to-end logic`) that ties the whole flow together. Reproducible:

```bash
dotnet run -- explain -s CodeTracer.sln --method "Agent.RunAsync" --depth 3 --max-methods 12 \
  --repo-url https://github.com/janjanusek/code_tracer/blob/main
```

> _Run: ~734 s (≈12 min) · 13 model calls (12 methods + the synthesis) · in 11506 / out 6455
> tokens · gemma4:latest, CPU-only, no GPU._

---

# Agent.RunAsync  ([Agent.cs:118](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L118))
`Task Agent.RunAsync(string solutionPath, string targetFile, string endpoint)`
_Deep explanation following the call chain (12 methods)._

## L0 · Agent.RunAsync  ([Agent.cs:118](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L118))
This method executes an asynchronous agent workflow designed to find a path or solution within a codebase, utilizing both deterministic analysis (like Roslyn) and a Large Language Model (LLM).

### Inputs and Outputs
*   **Inputs:**
    *   `solutionPath`: The path to the overall project solution.
    *   `targetFile`: The specific file being targeted for analysis.
    *   `endpoint`: An endpoint string used in the agent's initial setup.
*   **Outputs:** It returns a `Task`, indicating an asynchronous operation that completes when the agent finds a result or exhausts its attempts.
*   **Side Effects/State Changes:**
    *   It writes status messages to the console (`Console.WriteLine`).
    *   It updates the internal state variable `_lastPath` if a successful path is found during execution.

### Execution Flow (Numbered Steps)

1.  **Initialization and Pre-flight Check:**
    *   The method first calls `Bootstrap(targetFile, endpoint)` to generate an initial "seed" message for the agent.
    *   It performs a deterministic pre-flight check:
        *   If `_allPaths` is true, it attempts

## L1 · Agent.Bootstrap  ([Agent.cs:368](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L368))
This method generates a diagnostic report string designed to help guide the user in tracing a potential call chain within the system.

### Inputs and Outputs
*   **Inputs:**
    1.  `targetFile`: The file containing the destination methods (the "call target").
    2.  `endpoint`: The entry point or page model file name (the starting point of the trace).
*   **Output:** A `string` containing a formatted report detailing potential call paths and suggesting the next action (`find_path`).

### Side Effects
*   The method modifies the internal state variable `_pairs` by adding tuples representing suggested method pairs. This suggests it is contributing to a list of candidate relationships for later use in the system.

### Step-by-Step Explanation

1.  **Resolve Endpoint Path:** It first checks if the provided `endpoint` string ends with `.cshtml`. If it does, it assumes the corresponding code-behind handler lives in a file named by appending `.cs` (e.g., `MyPage.cshtml` becomes `MyPage.cshtml.cs`).
2.  **Gather Source Methods (`fromMethods`):**
    *   It checks if the resolved endpoint path exists on the filesystem using `File

## L1 · Agent.TryAllPaths  ([Agent.cs:450](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L450))


## L1 · Agent.TryAutoPath  ([Agent.cs:428](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L428))
This method attempts to automatically find a connection or usage path between pairs of methods defined in the system. It returns a formatted string detailing the findings, or a list of potential callers if no direct path is found.

### Inputs and Context
The method relies on several class members:
*   `_index`: An object (likely an indexer) used to query relationships within the codebase.
*   `_pairs`: A collection of tuples, where each tuple represents a candidate pair of methods (`fc`, `fm`, `tc`, `tm`).
*   `_repoUrl`: The URL of the repository (used in path searching).
*   `_withBodies`: A boolean flag determining if method bodies should be included in the search.

### Execution Steps and Logic

1.  **Initialization:** It first calls `Annotator()` to get an annotation object (`ann`), which is passed into subsequent searches.
2.  **Direct Path Search (Primary Loop):** The method iterates through every candidate pair (`p`) stored in `_pairs`.
    *   For each pair, it asynchronously calls `_index.FindPath()`, attempting to locate a direct path from the source methods (`p.fc`, `p.fm`) to the target methods (`p.tc`, `

## L1 · Agent.Finish  ([Agent.cs:317](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L317))
This method finalizes an agent's operation by presenting the determined path and optionally generating a summary, then saving the result to a file if configured.

Here is a step-by-step explanation:

1.  **Initialize Output:** The input `pathText` is cleaned (trimmed) and stored in the local variable `output`.
2.  **Generate Summary (Conditional):** If the class property `_summarize` is true AND the `output` text contains the string "PATH FOUND", the method performs the following:
    *   It prints a message to the error console (`[summary] summarizing the chain...`).
    *   It calls `SummarizeChain(pathText)` (a delegated call) to generate a summary.
    *   If the returned summary is not empty, it appends a formatted "Summary" section containing the summary text to the `output` variable.
3.  **Display Results:** The method prints a final header line to the standard console output indicating that the process is done and including the provided `reason`. It then prints the finalized `output` (which may include the summary).
4.  **Save Output (Side Effect):** If the class property `_outPath` is set and not empty, the method attempts to save the final `output` text (plus a newline) to that specified file path using `File.

## L1 · Agent.GetAction  ([Agent.cs:231](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L231))
This method is responsible for requesting a structured action (a tool call) from an external Language Model (LLM), handling potential failures, and retrying the request up to three times.

### Inputs and Outputs

*   **Input:** `List<ChatMsg> messages` – A list of chat messages representing the conversation history that guides the LLM's response.
*   **Output:** `Task<(string tool, JsonElement args, string raw)?>` – An asynchronous task that returns a tuple containing:
    1.  The name of the requested tool (`tool`, as a string).
    2.  A JSON element containing arguments for that tool (`args`).
    3.  The original raw text response from the LLM (`raw`).
    *   It returns `null` if all three attempts fail to produce a valid, usable action.

### Side Effects (State Modification)

If the LLM's output fails any validation step (JSON parsing, structure check, tool name validity, or argument validation), the method modifies the input `messages` list by adding two new entries:
1.  The raw, problematic response from the LLM (`assistant`).
2.  A corrective prompt instructing the user/LLM to fix the error and return valid JSON (`user`).

### Step-by-Step Execution

1.  **Initialization:** It sets up `ChatOptions` for the LLM call

## L1 · Agent.Canonical  ([Agent.cs:311](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L311))
This method takes a JSON structure (`JsonElement`) and produces a standardized, lowercase string representation of that structure. This is designed for reliable comparison (repeat detection).

Here is a step-by-step explanation:

1.  **Input:** The method accepts one parameter, `args`, which must be a structured JSON element (`JsonElement`).
2.  **Serialization (Delegation):** It first calls `JsonSerializer.Serialize(args)`. This process converts the in-memory JSON structure contained in `args` into its raw string representation (a standard JSON formatted string).
3.  **Normalization:** The resulting JSON string is then immediately passed to `.ToLowerInvariant()`. This method converts *all* characters in the serialized string to their lowercase invariant form.
4.  **Output:** The final result is a `string` that represents the canonical, all-lowercase version of the input JSON element.

**In summary:** It serializes the structured data into a string and then forces every character in that string to be lowercase, ensuring that case differences do not affect comparisons.

## L1 · Agent.SuggestPairs  ([Agent.cs:419](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L419))
This method generates a formatted string containing suggested tool usage pairs based on the stored list of potential connections.

1.  **Purpose:** The primary goal is to format up to three candidate "find path" suggestions into a structured string (likely JSON or similar syntax) that can be returned to guide further system actions.
2.  **Inputs/Reads:** It reads data from the private field `_pairs`, which is expected to hold a list of tuples, where each tuple represents a potential connection defined by four strings: `fromClass` (`fc`), `fromMethod` (`fm`), `toClass` (`tc`), and `toMethod` (`tm`).
3.  **Process:**
    *   It initializes an internal `StringBuilder` to accumulate the results.
    *   It iterates only over the first three elements of the `_pairs` list (using `Take(3)`).
    *   For each pair found, it constructs a

## L1 · Agent.Dispatch  ([Agent.cs:520](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L520))
This method acts as a central **dispatcher** that routes requests to various internal analysis tools managed by the `_index` object, using parameters provided in a JSON structure.

### Inputs and Outputs
*   **Inputs:**
    1.  `tool` (`string`): Specifies which functionality (e.g., "find\_symbol", "outline") should be executed.
    2.  `a` (`JsonElement`): A structured JSON object containing the parameters required by the specified tool.
*   **Output:** `Task<string>`: Returns a task that resolves to a string, which is the result of the requested analysis or an error message if the tool is unknown.

### Internal Logic and Side Effects (Delegation)

The method first defines two local helper functions for safely extracting data from the input JSON element (`a`):
1.  **`S(string k)`:** Attempts to retrieve a property named `k` from the JSON object as a string. Returns an empty string if the property is missing or not a string.
2.  **`I(string k, int def)`:** Attempts to retrieve a property named `k`

## L2 · RoslynIndex.MethodsInFile  ([RoslynIndex.cs:512](https://github.com/janjanusek/code_tracer/blob/main/RoslynIndex.cs#L512))
This method analyzes a specified source code file within the current solution structure and extracts a structured list containing the names and line numbers of all methods defined in that file.

### Inputs and Outputs

*   **Input:** `filePath` (a string representing the path to the source file).
*   **Output:** A `List<(string cls, string method, int line)>`. Each tuple represents a found member:
    *   `cls`: The name of the class containing the method.
    *   `method`: The name of the method itself.
    *   `line`: The 1-based line number where the method is declared.

### Execution Steps and Logic

1.  **Path Resolution:** It first determines the full, absolute path (`full`) for the input `filePath`. If the provided path is not already rooted (absolute), it prepends the `SolutionDir` to ensure correct resolution relative to the solution structure.
2.  **Document Identification:** The method iterates through all projects and documents within the overall solution (`_solution`). It uses a complex filtering mechanism involving `Path.GetFullPath` and string comparison to find the single document object (`doc`) whose file path matches the resolved input path (`full`), ignoring case differences. If no matching document is found, it returns an empty list immediately.
3.  **Syntax Tree Retrieval:** Once the correct document is identified

## L2 · Agent.Annotator  ([Agent.cs:486](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L486))


## L2 · RoslynIndex.FindPath  ([RoslynIndex.cs:281](https://github.com/janjanusek/code_tracer/blob/main/RoslynIndex.cs#L281))
This method determines if there is a sequence of calls (a "path") in the codebase that leads from a specified source method to a specified target method. It performs an upward traversal of the call graph, essentially asking: "What methods call this target method, and what methods call those callers, until we hit the starting point?"

### Inputs and Outputs

*   **Inputs:**
    *   `fromClass`, `fromMethod`: The fully qualified name (or identifiers) of the starting method.
    *   `toClass`, `toMethod`: The fully qualified name (or identifiers) of the target method.
    *   `maxNodes` (Default 3000): The maximum number of methods/symbols to explore during the search.
    *   `withBodies` (Default `false`): A boolean flag indicating whether the resulting path should include source code bodies.
    *   `repoUrl`: An optional URL used when rendering the final path.
    *   `annotate`: An optional callback function used to annotate or modify the path details during rendering.
*   **Output:**
    *   A `Task<string>` containing a string representation of the found call path, or an error message if the source/target methods are not found, or if no path is discovered within

## End-to-end logic
This system implements an advanced, AI-assisted static analysis agent designed to trace potential call paths or solutions within a large codebase. The execution flow is highly orchestrated, combining traditional compiler analysis (Roslyn) with graph traversal and Large Language Model (LLM) interaction.

Here is the complete end-to-end logic:

### 1. Initiation (`Agent.RunAsync`)
The process begins at `Agent.RunAsync`. This method takes the overall project solution path, the specific file being analyzed, and an initial endpoint string. Its primary role is to orchestrate the entire asynchronous agent workflow.

### 2. Initial Analysis & Path Generation (Bootstrap/Discovery)
The flow immediately moves to **`Agent.Bootstrap`**. This method uses the target file and the entry point (`endpoint`) to generate a detailed diagnostic report string. This initial report guides the user or subsequent steps by suggesting potential call paths, often prompting the system to execute a `find_path` action.

If an automatic path is needed, **`Agent.TryAutoPath`** attempts to automatically determine connections between methods defined in the system.

### 3. Core Path Finding (Static Analysis)
The core work of determining connectivity happens through specialized index and traversal methods:

*   **Indexing:** The process relies on `L2 RoslynIndex.MethodsInFile`. This method performs static analysis on a given source file path, extracting a structured list of all defined members (class name, method name, line number).
