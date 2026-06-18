# Agent.GetAction  ([Agent.cs:214](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L214))
`Task<(string tool, JsonElement args, string raw)?> Agent.GetAction(List<ChatMsg> messages)`
_Deep explanation following the call chain (6 methods)._

## L0 · Agent.GetAction  ([Agent.cs:214](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L214))
This method attempts to extract a structured action (a tool name and its arguments) from a large language model's output, ensuring the action is valid according to predefined rules. It handles potential failures by iteratively prompting the model for corrections.

### Inputs and Outputs

*   **Input:** `messages` (`List<ChatMsg>`) – A list of conversation messages that provide context to the LLM.
*   **Output:** A `Task` that returns a tuple containing:
    1.  `tool`: The name of the tool (string).
    2.  `args`: The arguments for the tool (a JSON element representing an object).
    3.  `raw`: The raw, unparsed text output from the LLM.
*   **Return Value:** Returns this tuple if a valid action is found; otherwise, it returns `null`.

### Process and Side Effects

The method operates within a loop that allows for up to three attempts (one initial attempt plus two correction attempts).

1.  **Initial Call:** It first calls `_llm.ChatAsync` using the provided conversation context (`messages`) and specific options designed to force the model's output into a structured format defined by `ActionSchema`.
2.  **JSON Parsing Attempt (Error Handling):**
    *   It attempts to parse the raw LLM output into a JSON structure.
    *   If parsing fails, it assumes the output is malformed JSON. It then **side-effects** the `messages` list by adding the raw output and a new user message instructing the model to return valid JSON. The loop continues to the next attempt.
3.  **Structure Validation:**
    *   It checks if the parsed root element is an object and contains a string property named `"tool"`. If not, it **side-effects** `messages` with the raw output and a user message detailing the required structure (a string "tool" field). The loop continues.
4.  **Tool Name Extraction:**
    *   It extracts the tool name from the JSON object and converts it to lowercase for comparison.
5.  **Argument Extraction:**
    *   It attempts to extract an `"args"` property, which must be a JSON object. If this property is missing or not an object, it defaults to `EmptyArgs`.
6.  **Tool Whitelist Check:**
    *   It checks if the extracted tool name exists within the predefined list of `AllowedTools`. If the tool is unknown,

## L1 · LlmClient.ChatAsync  ([LlmClient.cs:72](https://github.com/janjanusek/code_tracer/blob/main/LlmClient.cs#L72))
This method sends a chat message payload to an external Large Language Model (LLM) API endpoint and retrieves the model's text response.

### Inputs and Outputs

*   **Inputs:**
    1.  `messages`: A collection (`IEnumerable<ChatMsg>`) of messages representing the conversation history or prompt.
    2.  `options`: Optional settings (`ChatOptions?`) that can modify how the API call is made (e.g., temperature, model name).
    3.  `ct`: An optional `CancellationToken` to manage cancellation during asynchronous operations.
*   **Output:** A `Task<string>` which resolves to the text content of the LLM's response, or an empty string if no content can be extracted.

### Process and Side Effects

1.  **Payload Preparation:** It first ensures that `options` is initialized if null. Based on the internal API style (`_style`), it calls either `BuildOllama` or `BuildOpenAI` to generate the correct target URL and serialize the chat messages into a JSON payload string.
2.  **HTTP Request:** It constructs an HTTP POST request using the generated URL and JSON content, setting the content type to `application/json`.
3.  **API Call (Side Effect):** It asynchronously sends this request using the internal `_http` client (`HttpClient.SendAsync`).
4.  **Error Handling:** If the API response status code is not successful, it throws a detailed exception containing the HTTP status code and a truncated version of the error body.
5.  **Response Parsing:** Upon success, it reads the entire JSON response body as a string and parses it into a `JsonDocument`.
6.  **Content Extraction (Conditional Logic):** It inspects the root element of the parsed JSON document to find the actual message content. The extraction logic differs based on whether the API style is Ollama or OpenAI, navigating different nested structures:
    *   **Ollama:** Expects the structure `{ "message": { "content": "..." } }`.
    *   **OpenAI:** Expects the structure `{ "choices": [ { "message": { "content": "..." } } ] }` and accesses the first choice.
7.  **Return

## L1 · Agent.ValidateArgs  ([Agent.cs:266](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L266))
This method validates whether the provided JSON arguments (`args`) contain all the necessary fields for a given operational tool (`tool`).

### Inputs and Outputs
*   **Inputs:** `tool` (a string identifying the operation, e.g., "find\_path") and `args` (a `JsonElement` containing the input parameters).
*   **Output:** Returns `null` if all required arguments are present for the specified tool. Otherwise, it returns a descriptive error message listing the missing fields.

### Internal Logic and Delegation

The method relies on several local helper functions to perform its validation:

1.  **`Get(string k)`:** Attempts to retrieve a string value associated with key `k` from the input arguments (`args`). If the property is not found or is not a string, it returns an empty string.
2.  **`Has(string k)`:** Checks if the argument for key `k` exists and contains non-whitespace content (i.e., if the field was provided).
3.  **`Need(params string[] keys)`:** This is the core validation logic. It takes a list of required keys. It checks which of these keys are missing or empty using `Has()`. If one or more fields are missing

## L2 · LlmClient.BuildOllama  ([LlmClient.cs:102](https://github.com/janjanusek/code_tracer/blob/main/LlmClient.cs#L102))
This method constructs and returns a URL string and a JSON payload required to communicate with an Ollama API endpoint for chat interactions.

### Inputs and Outputs

*   **Inputs:**
    1.  `messages`: An `IEnumerable<ChatMsg>` containing the conversation history.
    2.  `o`: A `ChatOptions` object containing various configuration parameters (e.g., temperature, context size).
*   **Output:** A tuple containing:
    1.  A string representing the API URL (`_root/api/chat`).
    2.  A JSON formatted string payload ready for transmission.

### Functionality Breakdown

The method performs the following steps:

1.  **Build Options Dictionary (`opts`):** It initializes a dictionary to hold model options, setting default values for `temperature`, `num_ctx` (using `o.NumCtx` or the class's default `_defaultNumCtx`), and `repeat_penalty`. If `o.NumPredict` is set, it adds this value to the options.
2.  **Build Payload Dictionary (`payload`):** It initializes a main dictionary that structures the entire API request body:
    *   It sets the required `model` name using the class's private field `_model`.
    *   It hardcodes `"stream"` to `false`.
    *   It processes the input `messages`: it uses LINQ (`Select`) to transform each `ChatMsg` into an anonymous object containing only the `role` and `content`, and then converts this collection into a JSON array using `ToArray()`. This array is assigned to the `"messages"` key.
    *   It assigns the previously constructed options dictionary (`opts`) to the `"options"` key.
3.  **Handle Format (Conditional):** If the `ChatOptions` object (`o`) contains a specific format element (`JsonElement fmt`), this format is added directly to the main payload dictionary under the key `"format"`.
4.  **Serialization and Return:** The method uses `JsonSerializer.Serialize()` to convert the entire `payload` dictionary into a JSON string. It then returns this JSON string paired with the constructed API URL (`_root/api/chat`).

### Delegations (External Calls)

*   **`Dictionary<string, object?>`**: Used multiple times to structure key-value pairs for both model options and the final request payload.
*   **LINQ `Select()` / `ToArray()`**: Used to transform the list of input message objects (`IEnumerable<ChatMsg>`) into a structured array suitable for JSON serialization.
*   **`JsonSerializer.Serialize()`**: This is the core serialization step, converting the C# dictionary structure (`payload`) into the final JSON string output.

## L2 · LlmClient.BuildOpenAI  ([LlmClient.cs:124](https://github.com/janjanusek/code_tracer/blob/main/LlmClient.cs#L124))
This method constructs the necessary URL and JSON body required to communicate with an OpenAI-compatible chat completion endpoint.

### Inputs and Outputs
*   **Inputs:**
    1.  `messages`: A collection of messages (`IEnumerable<ChatMsg>`) representing the conversation history or prompt.
    2.  `o`: An object containing various configuration options for the API call (`ChatOptions`).
*   **Outputs:** A tuple containing two strings:
    1.  The full URL endpoint (`string url`).
    2.  The fully serialized JSON payload body (`string json`).

### Process Explanation (What it does)

1.  **Builds Base Payload:** It initializes a dictionary (`payload`) that contains the required core parameters for an OpenAI chat request:
    *   `model`: Set using the class field `_model`.
    *   `stream`: Hardcoded to `false` (indicating non-streaming response).
    *   `temperature`: Taken directly from the input options object (`o.Temperature`).
    *   `messages`: The input `messages` collection is transformed into a list of anonymous objects, each containing only the message's role and content.

2.  **Applies Optional Parameters:** It conditionally checks the input options (`o`) to add advanced parameters to the payload:
    *   **Max Tokens:** If `o.NumPredict` has an integer value, it adds this value as `"max_tokens"`.
    *   **JSON Format:** If `o.Format` is a JSON element (indicating structured output is desired), it adds a complex `"response_format"` object to the payload, specifying that the type must be `json_schema` and providing the schema definition from `o.Format`.

3.  **Constructs Output:**
    *   It constructs the final API endpoint URL by combining the class field `_root`, `/v1/chat/completions`.
    *   The entire `payload` dictionary is then serialized into a JSON string using `JsonSerializer.Serialize()`.

4.  **Returns Result:** It returns the constructed URL and the resulting JSON payload as a tuple.

## L2 · LlmClient.Truncate  ([LlmClient.cs:166](https://github.com/janjanusek/code_tracer/blob/main/LlmClient.cs#L166))
This method checks if an input string exceeds a specified maximum length (`max`). If it does, it truncates the string to fit `max` characters and appends an ellipsis (`...`) to indicate that content was removed.

Here is a step-by-step explanation:

1.  **Inputs:**
    *   `s`: The input string that might need truncation.
    *   `max`: An integer representing the maximum allowed length for the resulting string.
2.  **Logic Flow (Conditional Check):**
    *   The method first checks if the actual length of `s` (`s.Length`) is less than or equal to the provided limit (`max`).
3.  **Output Determination:**
    *   **If True (String fits):** The original string `s` is returned unchanged.
    *   **If False (String is too long):** The method executes a two-part operation:
        a. It uses substring indexing (`s[..max]`) to take only the first `max` characters of the input string `s`.
        b. It concatenates three literal dots (`...`) onto this truncated substring. This resulting, shorter string is then returned.
4.  **Side Effects:** The method is purely functional; it does not modify its inputs and has no observable side effects.

## End-to-end logic
The execution flow begins at **L0 Agent.GetAction**, which acts as the primary orchestration layer responsible for translating a conversational context into a structured, executable action (a tool call).

### 1. Initial Context and API Call (L0 $\rightarrow$ L1)

1.  **Entry Point:** The process starts with `Agent.GetAction`, receiving a list of conversation messages (`messages`).
2.  **LLM Interaction:** `Agent.GetAction` immediately delegates the core task to **L1 LlmClient.ChatAsync**. This method is responsible for communicating with an external Large Language Model (LLM) API endpoint.
3.  **API Preparation (Internal):** Before calling `ChatAsync`, the system must determine which LLM backend to use. If connecting to Ollama, **L2 LlmClient.BuildOllama** constructs the necessary API URL and JSON payload using the input messages and configuration options. Similarly, if using OpenAI, **L2 LlmClient.BuildOpenAI** performs this preparation for that specific endpoint.
4.  **Execution:** `ChatAsync` sends the prepared chat message payload to the external LLM service and waits for a raw text response from the model.

### 2. Parsing and Validation (L0 $\rightarrow$ L1)

1.  **Raw Output Reception:** The raw text output received from the
