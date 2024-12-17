// Required namespaces for the application
using System.Net;
using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure.AI.OpenAI;
using Azure.Communication;
using System.Text.RegularExpressions;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Newtonsoft.Json; 
using System.Net.Http;
using System.Text;
using PhoneNumbers;
using System.Collections.Generic;
// Create a new web application builder
var builder = WebApplication.CreateBuilder(args);
// Retrieve the application configuration
var configuration = builder.Configuration;
// Get various configuration settings from the environment, or throw exceptions if they're not set
var AZURE_COG_SERVICES_KEY = configuration["AZURE_COG_SERVICES_KEY"] ?? throw new Exception("AZURE_COG_SERVICES_KEY is not set");
var AZURE_COG_SERVICES_ENDPOINT = configuration["AZURE_COG_SERVICES_ENDPOINT"] ?? throw new Exception("AZURE_COG_SERVICES_ENDPOINT is not set");
var ACS_CONNECTION_STRING = configuration["ACS_CONNECTION_STRING"] ?? throw new Exception("ACS_CONNECTION_STRING is not set");
var ACS_PHONE_NUMBER = configuration["ACS_PHONE_NUMBER"] ?? throw new Exception("ACS_PHONE_NUMBER is not set");
var OPENAI_ENDPOINT = configuration["OPENAI_ENDPOINT"] ?? throw new Exception("OPENAI_ENDPOINT is not set");
var OPENAI_KEY = configuration["OPENAI_KEY"] ?? throw new Exception("OPENAI_KEY is not set");
var OPENAI_DEPLOYMENT_NAME = configuration["OPENAI_DEPLOYMENT_NAME"] ?? throw new Exception("OPENAI_DEPLOYMENT_NAME is not set");
var HOST_NAME = configuration["HOST_NAME"] ?? throw new Exception("HOST_NAME is not set");
// Create a new call automation client using the Azure Communication Service connection string
var callClient = new CallAutomationClient(ACS_CONNECTION_STRING);
// Create a dictionary to store chat sessions
var chatSessions = new Dictionary<string, List<ChatMessage>>();
List<string> dataList = new List<string>();
// Dictionary<string, string> dataList = new Dictionary<string, string>();
UserInfo userInfo = new UserInfo();
var phoneNumberData = "";
var product_interested ="";
// string full_name ="dkd";
// Register the call client and chat sessions as singletons with the DI container
builder.Services.AddSingleton(callClient);
builder.Services.AddSingleton(chatSessions);
var app = builder.Build();
app.UseDefaultFiles(); 
app.UseStaticFiles();
app.MapPost("/api/generate_prompt", async context =>
{
    // Retrieve the uploaded file from the request
    var file = context.Request.Form.Files[0];
    using var reader = new StreamReader(file.OpenReadStream());
    // Set up the Azure Cognitive Services Document Analysis client
    AzureKeyCredential credential = new AzureKeyCredential(AZURE_COG_SERVICES_KEY);
    DocumentAnalysisClient docClient = new DocumentAnalysisClient(new Uri(AZURE_COG_SERVICES_ENDPOINT), credential);
    // Analyze the document
    AnalyzeDocumentOperation operation = await docClient.AnalyzeDocumentAsync(
        WaitUntil.Completed, "prebuilt-read", reader.BaseStream);
    AnalyzeResult result = operation.Value;
    string prompt ="";

    prompt += result.Content;
    // Send the constructed prompt as the response
    await context.Response.WriteAsync(prompt);
});
app.MapPost("/api/call", async context =>
{
    // Deserialize the request body into a CallRequest object
    Console.WriteLine("calling the number");
    var data = await context.Request.ReadFromJsonAsync<CallRequest>();
    if (data == null) return;
    // Set up a call invite
    var id = data.call_id;
    Console.WriteLine($"Static ID: {id}");
    var callInvite = new CallInvite(
        new PhoneNumberIdentifier(data.PhoneNumber),
        new PhoneNumberIdentifier(ACS_PHONE_NUMBER)
    );
     // Generate a unique ID for the chat session
    var contextId = Guid.NewGuid().ToString();
    Console.WriteLine($"Prompt: {data.Prompt}");
    var messages = new[] {
        new ChatMessage(ChatRole.System, data.Prompt)
    };
    // Store the messages associated with the chat session
    chatSessions[contextId] = messages.ToList();   
    var createCallOptions = new CreateCallOptions(callInvite, new Uri($"{HOST_NAME}/api/callbacks/{contextId}?callerId={WebUtility.UrlEncode(data.PhoneNumber)}")) {
  CallIntelligenceOptions = new CallIntelligenceOptions() {
    CognitiveServicesEndpoint = new Uri(AZURE_COG_SERVICES_ENDPOINT)
  }
};
    // Create the call
    var result = await callClient.CreateCallAsync(createCallOptions); 
    Console.WriteLine($"Call created successfully: {result}");
});
app.MapPost("/api/callbacks/{contextId}", async (context) =>
{
    Console.WriteLine("callback contextId");
        // Parse incoming cloud events
    var cloudEvents = await context.Request.ReadFromJsonAsync<CloudEvent[]>() ?? Array.Empty<CloudEvent>();
    var contextId = context.Request.RouteValues["contextId"]?.ToString() ?? "";
    var callerId = context.Request.Query["callerId"].ToString() ?? "";

    // Create a UserInfo object to store the user's information

    foreach (var cloudEvent in cloudEvents)
    {
        // Console.WriteLine("cloudEvent"+cloudEvent);
        // Parse the cloud event to get the call event details
        CallAutomationEventBase callEvent = CallAutomationEventParser.Parse(cloudEvent);
        var callConnection = callClient.GetCallConnection(callEvent.CallConnectionId);
        var callConnectionMedia = callConnection.GetCallMedia();
        Console.WriteLine("callEvent,"+CallAutomationEventParser.Parse(cloudEvent));

        var messages = chatSessions[contextId];
        var phoneId = new PhoneNumberIdentifier(callerId); 
        if (callEvent is CallConnected)
        {
            Console.WriteLine("call got connected");
            phoneNumberData = phoneId.PhoneNumber;
            var response = await GetChatGPTResponse(messages);
            messages.Add(new ChatMessage(ChatRole.Assistant, response));
            await SayAndRecognize(callConnectionMedia, phoneId, response);
        }
        if (callEvent is RecognizeCompleted recogEvent 
            && recogEvent.RecognizeResult is SpeechResult speech_result)
        {
            // Console.WriteLine($"chatSessions: {chatSessions}");
            // Console.WriteLine("chatSessions Content:");
            // foreach (var session in chatSessions)
            // {
            //     // Console.WriteLine($"Session ID: {session.Key}");
            //     foreach (var message in session.Value)
            //     {
            //         Console.WriteLine($"Role: {message.Role}, Content: {message.Content}");
            //     }
            // }
            string recognizedSpeech = speech_result.Speech.ToLower();
            Console.WriteLine($"Test001: {recognizedSpeech}");
            dataList.Add(recognizedSpeech);
            // Console.WriteLine($"dataList: {dataList}");
            // Convert to lowercase for easier matching
                        // Check if the user said something like "end call"
            if (recognizedSpeech.Contains("end the call") || recognizedSpeech.Contains("terminate"))
            {
                // Hang up the call
                await callConnection.HangUpAsync(true);
            }
            // Read the response content
            if (IsOutOfContext(recognizedSpeech))
            {
                        Console.WriteLine("out of box");
                var outOfContextResponse = "I am here to help with credit card inquiries. How can I assist you with that?";
                await SayAndRecognize(callConnectionMedia, phoneId, outOfContextResponse);
            }
            else{
            // var responseBody = await push_response.Content.ReadAsStringAsync();
            // Console.WriteLine("Response: " + responseBody);
            messages.Add(new ChatMessage(ChatRole.User, speech_result.Speech));

            // Console.WriteLine("Response: " + responseBody);
            var response = await GetChatGPTResponse(messages);
            messages.Add(new ChatMessage(ChatRole.Assistant, response));
            await SayAndRecognize(callConnectionMedia, phoneId, response);
            }

            
        }
        if (callEvent is CallDisconnected)
        {
        Console.WriteLine("callEvent disconnected by user");
        }
        if (callEvent is RecognizeFailed)
        {
        Console.WriteLine("not able to understand asking it again");
        messages.Add(new ChatMessage(ChatRole.User, "I'm sorry, I couldn't understand that. Could you please repeat?"));
        // Console.WriteLine("Response: " + responseBody);
        var response = await GetChatGPTResponse(messages);
        messages.Add(new ChatMessage(ChatRole.Assistant, response));
        await SayAndRecognize(callConnectionMedia, phoneId, response);
        }

    }   
});
app.Run();
// // // Function to get a response from OpenAI's ChatGPT
async Task<string> GetChatGPTResponse(List<ChatMessage> messages)
{
    Console.WriteLine($"<<<<<<<<<<<<<-In the GPT ->>>>>>>>>>>>>>");
    // Set up the OpenAI client
    OpenAIClient openAIClient = new OpenAIClient(
        new Uri(OPENAI_ENDPOINT),
        new AzureKeyCredential(OPENAI_KEY));
    // Get a chat completion from OpenAI's ChatGPT
    var chatCompletionsOptions = new ChatCompletionsOptions(messages);
    Response<ChatCompletions> response = await openAIClient.GetChatCompletionsAsync(
        deploymentOrModelName: OPENAI_DEPLOYMENT_NAME,
        chatCompletionsOptions);
    Console.WriteLine(response);

    Console.WriteLine("GPT RES"+response.Value.Choices[0].Message.Content);
    var dataToSend123 = response.Value.Choices[0].Message.Content;  // Sending the full collected data list
    Console.WriteLine("Sending collected data: " + JsonConvert.SerializeObject(dataToSend123));
    Console.WriteLine($"dataToSend123:{dataToSend123}");
        // Simple out-of-context detection logic
    if (IsOutOfContext(response.Value.Choices[0].Message.Content)) 
    {
        Console.WriteLine("out of box");
        // Respond with a gentle redirect
        return "I'm here to assist with your credit card inquiry. Could you please let me know your question about your credit card?";
    }
    // Use regex to extract the JSON part from the text
    string pattern = @"\{.*\}";
    Match match = Regex.Match(dataToSend123, pattern, RegexOptions.Singleline);

    if (match.Success)
    {
        // Extract the matched JSON string
        string jsonData = match.Value;

        try
        {
            //trysample
            // Output the extracted JSON data
            Console.WriteLine("Extracted JSON:");
            // Deserialize the JSON string into a dynamic object
            var extractedData = JsonConvert.DeserializeObject<dynamic>(jsonData);
            Console.WriteLine($"extractedData {extractedData}");
            // Console.WriteLine($"extractedData {extractedData.is_bike_sale_related}");
            // Console.WriteLine($"extractedData {extractedData.is_interview_questions}");
            if (extractedData.ContainsKey("is_interview_questions"))
            {
                // Handle case when "is_interview_questions" is found
                Console.WriteLine($"is_interview_questions");
                if (extractedData.is_interview_questions.ToString().ToLower() == "true")
                    {
                        Console.WriteLine("Resume Project");
                        Console.WriteLine("All details have been successfully collected.");
                        return $"Thanks for the time {extractedData.full_name}. Based on the information provided, I have collected all the necessary details for your job at Comportement. We will get back once we have reviewed your answers. Good Day";
                    }
                else
                    {
                        Console.WriteLine("else condition!!!!");
                        if (extractedData.is_interview_questions.ToString().ToLower() == "creditcardquestions")
                        {
                        Console.WriteLine("Sales Assitantant data dumping API");
                        var dataToSend = new
                        {
                        phoneNumber = phoneNumberData,
                        full_name = extractedData.full_name,
                        interested_for_credit_card = extractedData.response_for_question_1,
                        annual_income = extractedData.response_for_question_2,
                        employment_status = extractedData.response_for_question_3,
                        any_current_credit_card= extractedData.response_for_question_4,
                        preferred_credit_limit = extractedData.response_for_question_5
                        };
                        Console.WriteLine("---------data sent format---------");
                        Console.WriteLine(JsonConvert.SerializeObject(dataToSend));
                        // Set the URL and headers
                        var url = "https://script.google.com/macros/s/AKfycbyi4HJQdhH3etfEW4z6llR1H7m0b-TZ4KSuWUbvWUJelsXMgmLc7R-SrRwtnLqIx7zv5Q/exec";
                        var client = new HttpClient();
                        var headers = new StringContent(JsonConvert.SerializeObject(dataToSend), Encoding.UTF8, "application/json");
                        // Make the POST request
                        var push_response = await client.PostAsync(url, headers);
                        // Ensure the request was successful
                        push_response.EnsureSuccessStatusCode();
                
                        return $"Based on the information provided, I have collected all the necessary details for your credit card inquiry. Here is a summary:"+ $"Full Name: {extractedData.full_name},"+$"Interested for Credit Card: {extractedData.response_for_question_1}"
                        +$"Annual Income: {extractedData.response_for_question_2}" +$"Employment Status: {extractedData.response_for_question_3}" +$"Any Current Credit Card: {extractedData.response_for_question_4}"+$"Preferred Credit Limit: {extractedData.response_for_question_5}"+" If there are any corrections or changes, please let me know.";
                        }
                        else if(extractedData.is_interview_questions.ToString().ToLower() == "healthcareinsurancequestions"){
                        
                        Console.WriteLine("Health insurance Sales Assitant data dumping API");
                        var dataToSend = new
                        {
                        phoneNumber = phoneNumberData,
                        full_name = extractedData.full_name,
                        interested_in_insurance = extractedData.response_for_question_1,
                        annual_income = extractedData.response_for_question_2,
                        employment_status = extractedData.response_for_question_3,
                        current_health_insurance= extractedData.response_for_question_4,
                        preferred_coverage_amount = extractedData.response_for_question_5
                        };
                        Console.WriteLine("---------data sent format---------");
                        Console.WriteLine(JsonConvert.SerializeObject(dataToSend));
                        // Set the URL and headers
                        var url = "https://script.google.com/macros/s/AKfycbw4K_sdT0NoYzTB6W9-WR-4G2ObIBX72rDYOUaqt5rZxgsQ7DxmrO86SAYq7MuzpFuTrQ/exec";
                        var client = new HttpClient();
                        var headers = new StringContent(JsonConvert.SerializeObject(dataToSend), Encoding.UTF8, "application/json");
                        // Make the POST request
                        var push_response = await client.PostAsync(url, headers);
                        // Ensure the request was successful
                        push_response.EnsureSuccessStatusCode();
                
                        return $"Based on the information provided, I have collected all the necessary details for your health insurance inquiry. Here is a summary:"+ $"Full Name: {extractedData.full_name},"+$"Interested for Health Insurance: {extractedData.response_for_question_1}"
                        +$"Annual Income: {extractedData.response_for_question_2}" +$"Employment Status: {extractedData.response_for_question_3}" +$"Any other Health Insurance: {extractedData.response_for_question_4}"+$"Preferred coverage Amount: {extractedData.response_for_question_5}"+" If there are any corrections or changes, please let me know.";
                        }
                        //telecommunication 
                        else if(extractedData.is_interview_questions.ToString().ToLower() == "telecomcompanyquestions"){
                        
                        Console.WriteLine("Telecom insurenance Sales Assitant data dumping API");
                        var dataToSend = new
                        {
                        phoneNumber = phoneNumberData,
                        full_name = extractedData.full_name,
                        interested_in_new_plan = extractedData.response_for_question_1,
                        monthly_data_usage = extractedData.response_for_question_2,
                        current_provider = extractedData.response_for_question_3,
                        phone_usage_type= extractedData.response_for_question_4,
                        preferred_monthly_budget = extractedData.response_for_question_5
                        };
                        Console.WriteLine("---------data sent format---------");
                        Console.WriteLine(JsonConvert.SerializeObject(dataToSend));
                        // Set the URL and headers
                        var url = "https://script.google.com/macros/s/AKfycbwIB9XtYITuVOJ_bO47sh0iwdSeqjHVuOvuKTiLTxLXWA8fTAxiEjcbgWiT857q9mep/exec";
                        var client = new HttpClient();
                        var headers = new StringContent(JsonConvert.SerializeObject(dataToSend), Encoding.UTF8, "application/json");
                        // Make the POST request
                        var push_response = await client.PostAsync(url, headers);
                        // Ensure the request was successful
                        push_response.EnsureSuccessStatusCode();
                
                        return $"Based on the information provided, I have collected all the necessary details for your Telecom Plan inquiry. Here is a summary:"+ $"Full Name: {extractedData.full_name},"+$"Interested In New Plan: {extractedData.response_for_question_1}"
                        +$"Monthly Data Usage: {extractedData.response_for_question_2}" +$"Current Provider: {extractedData.response_for_question_3}" +$"Phone Usage Type: {extractedData.response_for_question_4}"+$"Monthly Budget: {extractedData.response_for_question_5}"+" If there are any corrections or changes, please let me know.";
                        }
                        //REALESTATE QUESTIONS
                        else if(extractedData.is_interview_questions.ToString().ToLower() == "realestatequestions"){
                        
                        Console.WriteLine("RealEState Sales Assitant data dumping API");
                        var dataToSend = new
                        {
                        phoneNumber = phoneNumberData,
                        full_name = extractedData.full_name,
                        interested_in_renting = extractedData.response_for_question_1,
                        desired_location = extractedData.response_for_question_2,
                        monthly_income = extractedData.response_for_question_3,
                        property_type= extractedData.response_for_question_4,
                        budget_range = extractedData.response_for_question_5
                        };
                        Console.WriteLine("---------data sent format---------");
                        Console.WriteLine(JsonConvert.SerializeObject(dataToSend));
                        // Set the URL and headers
                        var url = "https://script.google.com/macros/s/AKfycbz1KHkRdTtVukOZqTt78oQnH2yHDZ0UuCamXS9MkQNv3hK0fLdqPjqLk9VA1Su6Omaq/exec";
                        var client = new HttpClient();
                        var headers = new StringContent(JsonConvert.SerializeObject(dataToSend), Encoding.UTF8, "application/json");
                        // Make the POST request
                        var push_response = await client.PostAsync(url, headers);
                        // Ensure the request was successful
                        push_response.EnsureSuccessStatusCode();
                
                        return $"Based on the information provided, I have collected all the necessary details for your Real Estate Location Rent inquiry. Here is a summary:"+ $"Full Name: {extractedData.full_name},"+$"interested in renting a property: {extractedData.response_for_question_1}"
                        +$"Property Location: {extractedData.response_for_question_2}" +$"Monthly Income: {extractedData.response_for_question_3}" +$"Property Type: {extractedData.response_for_question_4}"+$"Budget Range: {extractedData.response_for_question_5}"+" If there are any corrections or changes, please let me know.";
                        }
                        
                    
                    }

            }
            else if (extractedData.ContainsKey("is_bike_sale_related"))
            {
                // Handle case when "is_bike_sale_related" is found
                Console.WriteLine($"Bike Sale Value");
                // Console.WriteLine("RealEState Sales Assitant data dumping API");
                // var dataToSend = new
                // {
                // phoneNumber = phoneNumberData,
                // full_name = extractedData.full_name,
                // interested_in_renting = extractedData.response_for_question_1,
                // desired_location = extractedData.response_for_question_2,
                // monthly_income = extractedData.response_for_question_3,
                // property_type= extractedData.response_for_question_4,
                // budget_range = extractedData.response_for_question_5
                // };
                // Console.WriteLine("---------data sent format---------");
                // Console.WriteLine(JsonConvert.SerializeObject(dataToSend));
                // // Set the URL and headers
                // var url = "https://script.google.com/macros/s/AKfycbz1KHkRdTtVukOZqTt78oQnH2yHDZ0UuCamXS9MkQNv3hK0fLdqPjqLk9VA1Su6Omaq/exec";
                // var client = new HttpClient();
                // var headers = new StringContent(JsonConvert.SerializeObject(dataToSend), Encoding.UTF8, "application/json");
                // // Make the POST request
                // var push_response = await client.PostAsync(url, headers);
                // // Ensure the request was successful
                // push_response.EnsureSuccessStatusCode();
        
                return $"Based on the information provided, I have collected all the necessary details for your RS660 inquiry.If there are any corrections or changes, please let me know.";

            }
            // Perform a true/false condition check on 'all_details_collected'
            // string is_interview_questions = extractedData.is_interview_questions;
            else if (extractedData.ContainsKey("is_jewellery_sale_related"))
            {
                // Handle case when "is_bike_sale_related" is found
                Console.WriteLine($"is_jewellery_sale_related");
                var dataToSend = new
                {
                phoneNumber = phoneNumberData,
                product_interested = extractedData.product_interested
                };
                Console.WriteLine("---------data sent format---------");
                Console.WriteLine(JsonConvert.SerializeObject(dataToSend));
                // Set the URL and headers
                var url = "https://script.google.com/macros/s/AKfycbxuDioMYgRLCGpwb1Kwp8bPpinRlKtaUXHF7CpBmMJdcdGljm_b6jmtulHikiNzbvVB/exec";
                var client = new HttpClient();
                var headers = new StringContent(JsonConvert.SerializeObject(dataToSend), Encoding.UTF8, "application/json");
                // Make the POST request
                var push_response = await client.PostAsync(url, headers);
                // Ensure the request was successful
                push_response.EnsureSuccessStatusCode();
                //extracting variable for interact
                product_interested = extractedData.product_interested.ToString().ToLower();
                Console.WriteLine($"product_interested->{product_interested}");
                var countryCode = ExtractCountryCode(phoneNumberData);
                Console.WriteLine($"countryCode->{countryCode.Item1}");
                Console.WriteLine($"phonenumber->{countryCode.Item2}");
                string[] ProductList = { "bracelet","bracelets", "ring" ,"rings","bracelet below 20 grams","bracelet 25-30 grams","bracelet 30-35 grams","bracelet 35-40 grams","bracelet 40 grams","men's ring","ladies ring","bracelet 20 grams","bracelet 20-25 grams","bracelet (20-25 grams)","bracelet (25-30 grams)","bracelet (30-35 grams)","bracelet (35-40 grams)","ladies rings","men's rings"};
                // Convert the product string into an array of items
                string[] selectedproductList = product_interested.Split(new[] { " and " }, StringSplitOptions.None);
                static Tuple<string, string> ExtractCountryCode(string phoneNumber)                
                {
                    try
                    {
                        var phoneNumberUtil = PhoneNumberUtil.GetInstance();
                        var parsedNumber = phoneNumberUtil.Parse(phoneNumber, null);

                        // Get the country code and national (subscriber) number
                        string countryCode = parsedNumber.CountryCode.ToString();
                        string localNumber = parsedNumber.NationalNumber.ToString();

                        return Tuple.Create(countryCode, localNumber);
                    }
                    catch (NumberParseException)
                    {
                        return null; // Return null if parsing fails
                    }
                }
                foreach (var i in ProductList)
                {
                    if (selectedproductList.Contains(i))
                    {
                        // Make API call for the recognized product
                        Console.WriteLine($"push notification API => send for {i}");
                        if ((string.Equals(i, "bracelet", StringComparison.OrdinalIgnoreCase))||(string
                        .Equals(i, "bracelets", StringComparison.OrdinalIgnoreCase)))
                        {
                            Console.WriteLine($"push notification API for Bracelet");
                            var interclient = new HttpClient();
                            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.interakt.ai/v1/public/message/");
                            request.Headers.Add("Authorization", "Basic TTNsNlFUWnZlZm1sNzFBeWZ3cGxJSWhaTU9KWktHX0VZSzZZMjMzdWlwbzo=");
                            var content = new StringContent($@"{{
                            ""countryCode"": ""{countryCode.Item1}"",
                            ""phoneNumber"": ""{countryCode.Item2}"",
                            ""callbackData"": ""Test for template"",
                            ""type"": ""Template"",
                            ""template"": {{
                                ""name"": ""bracelet_lm"",
                                ""languageCode"": ""en"",
                                ""headerValues"": [
                                    ""https://interaktprodmediastorage.blob.core.windows.net/mediaprodstoragecontainer/dd2b315b-a1f6-4f2b-a3f7-f6a67718d9c9/message_template_media/6cPWliXZEJVn/Bracelet%2025-30%20gms.pdf?se=2029-11-24T07%3A03%3A29Z&sp=rt&sv=2019-12-12&sr=b&sig=kYqGp79FcOaAWukTt4KWiNE1XTRfdmwZtF8l/Y/bOXE%3D""
                                ],
                                ""bodyValues"": [
                                    ""Anoop MR""
                                ]
                            }}
                        }}", null, "application/json");
                            request.Content = content;
                            var interresponse = await interclient.SendAsync(request);
                            interresponse.EnsureSuccessStatusCode();
                            Console.WriteLine(await interresponse.Content.ReadAsStringAsync());
                        }
                        else if ((string.Equals(i, "Rings", StringComparison.OrdinalIgnoreCase))||(string
                        .Equals(i, "Ring", StringComparison.OrdinalIgnoreCase)))
                        {
                            Console.WriteLine($"push notification API for Ring");
                            var interclient = new HttpClient();
                            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.interakt.ai/v1/public/message/");
                            request.Headers.Add("Authorization", "Basic TTNsNlFUWnZlZm1sNzFBeWZ3cGxJSWhaTU9KWktHX0VZSzZZMjMzdWlwbzo=");
                            var content = new StringContent($@"{{
                            ""countryCode"": ""{countryCode.Item1}"",
                            ""phoneNumber"": ""{countryCode.Item2}"",
                            ""callbackData"": ""Test for template"",
                            ""type"": ""Template"",
                            ""template"": {{
                                ""name"": ""ladies_ring"",
                                ""languageCode"": ""en"",
                                ""headerValues"": [
                                    ""https://interaktprodmediastorage.blob.core.windows.net/mediaprodstoragecontainer/dd2b315b-a1f6-4f2b-a3f7-f6a67718d9c9/message_template_media/OKM6nb0v6QMu/Ladies%20Ring.pdf?se=2029-11-24T06%3A45%3A30Z&sp=rt&sv=2019-12-12&sr=b&sig=DWLSppGZxeMqVcUgynEwMRJHMmO42lFGkl2BZ2S8VIA%3D""
                                ],
                                ""bodyValues"": [
                                    ""Anoop MR""
                                ]
                            }}
                        }}", null, "application/json");
                            request.Content = content;
                            var interresponse = await interclient.SendAsync(request);
                            interresponse.EnsureSuccessStatusCode();
                            Console.WriteLine(await interresponse.Content.ReadAsStringAsync());
                        }
                        else if((string.Equals(i, "bracelet 20-25 grams", StringComparison.OrdinalIgnoreCase))||(string.Equals(i, "bracelet (20-25 grams)", StringComparison.OrdinalIgnoreCase)))
                        {
                            Console.WriteLine($"push notification API for bracelet (20-25 grams)");
                            var interclient = new HttpClient();
                            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.interakt.ai/v1/public/message/");
                            request.Headers.Add("Authorization", "Basic TTNsNlFUWnZlZm1sNzFBeWZ3cGxJSWhaTU9KWktHX0VZSzZZMjMzdWlwbzo=");
                            var content = new StringContent($@"{{
                            ""countryCode"": ""{countryCode.Item1}"",
                            ""phoneNumber"": ""{countryCode.Item2}"",
                            ""callbackData"": ""Test for template"",
                            ""type"": ""Template"",
                            ""template"": {{
                                ""name"": ""bracelet25_30"",
                                ""languageCode"": ""en"",
                                ""headerValues"": [
                                    ""https://interaktprodmediastorage.blob.core.windows.net/mediaprodstoragecontainer/dd2b315b-a1f6-4f2b-a3f7-f6a67718d9c9/message_template_media/iCsEJNkm69Yh/Bracelet%2020-25%20gms.pdf?se=2029-11-26T09%3A23%3A52Z&sp=rt&sv=2019-12-12&sr=b&sig=mbhQ5DTzEhgIhQtTls4TOzRj7VgfGYeaFY9dKDEByHo%3D""
                                ],
                                ""bodyValues"": [
                                    ""Anoop MR""
                                ]
                            }}
                        }}", null, "application/json");
                            request.Content = content;
                            var interresponse = await interclient.SendAsync(request);
                            interresponse.EnsureSuccessStatusCode();
                            Console.WriteLine(await interresponse.Content.ReadAsStringAsync());
                        }
                        else if((string.Equals(i, "bracelet below 20 grams", StringComparison.OrdinalIgnoreCase)))
                        {
                            Console.WriteLine($"push notification API for bracelet below 20 grams");
                            var interclient = new HttpClient();
                            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.interakt.ai/v1/public/message/");
                            request.Headers.Add("Authorization", "Basic TTNsNlFUWnZlZm1sNzFBeWZ3cGxJSWhaTU9KWktHX0VZSzZZMjMzdWlwbzo=");
                            var content = new StringContent($@"{{
                            ""countryCode"": ""{countryCode.Item1}"",
                            ""phoneNumber"": ""{countryCode.Item2}"",
                            ""callbackData"": ""Test for template"",
                            ""type"": ""Template"",
                            ""template"": {{
                                ""name"": ""braceletbelow20gms"",
                                ""languageCode"": ""en"",
                                ""headerValues"": [
                                    ""https://interaktprodmediastorage.blob.core.windows.net/mediaprodstoragecontainer/dd2b315b-a1f6-4f2b-a3f7-f6a67718d9c9/message_template_media/w0dMEOMaHsWc/Bracelet%20Below%2020%20gms.pdf?se=2029-11-26T07%3A20%3A47Z&sp=rt&sv=2019-12-12&sr=b&sig=lPfXF%2B7umNz4sULAcZ2nNg2WieKwaYFJQjNZH%2Bn1UOc%3D""
                                ],
                                ""bodyValues"": [
                                    ""Anoop MR""
                                ]
                            }}
                        }}", null, "application/json");
                            request.Content = content;
                            var interresponse = await interclient.SendAsync(request);
                            interresponse.EnsureSuccessStatusCode();
                            Console.WriteLine(await interresponse.Content.ReadAsStringAsync());
                        }
                        else if((string.Equals(i, "bracelet 25-30 grams", StringComparison.OrdinalIgnoreCase))||(string.Equals(i, "bracelet (25-30 grams)", StringComparison.OrdinalIgnoreCase)))
                        {
                            Console.WriteLine($"push notification API for bracelet 25-30 grams");
                            var interclient = new HttpClient();
                            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.interakt.ai/v1/public/message/");
                            request.Headers.Add("Authorization", "Basic TTNsNlFUWnZlZm1sNzFBeWZ3cGxJSWhaTU9KWktHX0VZSzZZMjMzdWlwbzo=");
                            var content = new StringContent($@"{{
                            ""countryCode"": ""{countryCode.Item1}"",
                            ""phoneNumber"": ""{countryCode.Item2}"",
                            ""callbackData"": ""Test for template"",
                            ""type"": ""Template"",
                            ""template"": {{
                                ""name"": ""bracelet25_30_gms"",
                                ""languageCode"": ""en"",
                                ""headerValues"": [
                                    ""https://interaktprodmediastorage.blob.core.windows.net/mediaprodstoragecontainer/dd2b315b-a1f6-4f2b-a3f7-f6a67718d9c9/message_template_media/yUAAsYfrNyxU/Bracelet%2025-30%20gms.pdf?se=2029-11-26T07%3A09%3A23Z&sp=rt&sv=2019-12-12&sr=b&sig=ru4RIWX3gGKH227sde5D2q0abOXbteHZjlVjVj4ifzQ%3D""
                                ],
                                ""bodyValues"": [
                                    ""Anoop MR""
                                ]
                            }}
                        }}", null, "application/json");
                            request.Content = content;
                            var interresponse = await interclient.SendAsync(request);
                            interresponse.EnsureSuccessStatusCode();
                            Console.WriteLine(await interresponse.Content.ReadAsStringAsync());
                        }
                        else if((string.Equals(i, "bracelet 30-35 grams", StringComparison.OrdinalIgnoreCase))||(string.Equals(i, "bracelet (30-35 grams)", StringComparison.OrdinalIgnoreCase)))
                        {
                            Console.WriteLine($"push notification API for bracelet 30-35 grams");
                            var interclient = new HttpClient();
                            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.interakt.ai/v1/public/message/");
                            request.Headers.Add("Authorization", "Basic TTNsNlFUWnZlZm1sNzFBeWZ3cGxJSWhaTU9KWktHX0VZSzZZMjMzdWlwbzo=");
                            var content = new StringContent($@"{{
                            ""countryCode"": ""{countryCode.Item1}"",
                            ""phoneNumber"": ""{countryCode.Item2}"",
                            ""callbackData"": ""Test for template"",
                            ""type"": ""Template"",
                            ""template"": {{
                                ""name"": ""bracelet30_35gms"",
                                ""languageCode"": ""en"",
                                ""headerValues"": [
                                    ""https://interaktprodmediastorage.blob.core.windows.net/mediaprodstoragecontainer/dd2b315b-a1f6-4f2b-a3f7-f6a67718d9c9/message_template_media/z40VOhuBFFhZ/Bracelet%2030-35%20gms.pdf?se=2029-11-26T07%3A19%3A22Z&sp=rt&sv=2019-12-12&sr=b&sig=h6DGpK7ShHAwxeTRPcYFwgLn1j8BuL1FqFCzMqXCDuE%3D""
                                ],
                                ""bodyValues"": [
                                    ""Anoop MR""
                                ]
                            }}
                        }}", null, "application/json");
                            request.Content = content;
                            var interresponse = await interclient.SendAsync(request);
                            interresponse.EnsureSuccessStatusCode();
                            Console.WriteLine(await interresponse.Content.ReadAsStringAsync());
                        }
                        else if((string.Equals(i, "bracelet 35-40 grams", StringComparison.OrdinalIgnoreCase))||(string.Equals(i, "bracelet (35-40 grams)", StringComparison.OrdinalIgnoreCase)))
                        {
                            Console.WriteLine($"push notification API for bracelet 35-40 grams");
                            var interclient = new HttpClient();
                            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.interakt.ai/v1/public/message/");
                            request.Headers.Add("Authorization", "Basic TTNsNlFUWnZlZm1sNzFBeWZ3cGxJSWhaTU9KWktHX0VZSzZZMjMzdWlwbzo=");
                            var content = new StringContent($@"{{
                            ""countryCode"": ""{countryCode.Item1}"",
                            ""phoneNumber"": ""{countryCode.Item2}"",
                            ""callbackData"": ""Test for template"",
                            ""type"": ""Template"",
                            ""template"": {{
                                ""name"": ""bracelet_35_40_gms"",
                                ""languageCode"": ""en"",
                                ""headerValues"": [
                                    ""https://interaktprodmediastorage.blob.core.windows.net/mediaprodstoragecontainer/dd2b315b-a1f6-4f2b-a3f7-f6a67718d9c9/message_template_media/o4YLxmzWkk5r/Bracelet%2035-40%20gms.pdf?se=2029-11-26T06%3A07%3A58Z&sp=rt&sv=2019-12-12&sr=b&sig=l2e251tYCljTERWHgfplzBmdUXyGCKM7PB4YNL%2BbBLw%3D""
                                ],
                                ""bodyValues"": [
                                    ""Anoop MR""
                                ]
                            }}
                        }}", null, "application/json");
                            request.Content = content;
                            var interresponse = await interclient.SendAsync(request);
                            interresponse.EnsureSuccessStatusCode();
                            Console.WriteLine(await interresponse.Content.ReadAsStringAsync());
                        }
                        else if((string.Equals(i, "bracelet 40 grams", StringComparison.OrdinalIgnoreCase)))
                        {
                            Console.WriteLine($"push notification API for bracelet 40 grams");
                            var interclient = new HttpClient();
                            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.interakt.ai/v1/public/message/");
                            request.Headers.Add("Authorization", "Basic TTNsNlFUWnZlZm1sNzFBeWZ3cGxJSWhaTU9KWktHX0VZSzZZMjMzdWlwbzo=");
                            var content = new StringContent($@"{{
                            ""countryCode"": ""{countryCode.Item1}"",
                            ""phoneNumber"": ""{countryCode.Item2}"",
                            ""callbackData"": ""Test for template"",
                            ""type"": ""Template"",
                            ""template"": {{
                                ""name"": ""bracelet40gmsplus"",
                                ""languageCode"": ""en"",
                                ""headerValues"": [
                                    ""https://interaktprodmediastorage.blob.core.windows.net/mediaprodstoragecontainer/dd2b315b-a1f6-4f2b-a3f7-f6a67718d9c9/message_template_media/hIeQF5LiiAjq/Bracelet%2040%20gms%2B.pdf?se=2029-11-26T07%3A14%3A53Z&sp=rt&sv=2019-12-12&sr=b&sig=2RYmCxqzRVbn57Xoy9pLKyHx/jcoM6P/KLfT4Pm0jHU%3D""
                                ],
                                ""bodyValues"": [
                                    ""Anoop MR""
                                ]
                            }}
                        }}", null, "application/json");
                            request.Content = content;
                            var interresponse = await interclient.SendAsync(request);
                            interresponse.EnsureSuccessStatusCode();
                            Console.WriteLine(await interresponse.Content.ReadAsStringAsync());
                        }
                        else if((string.Equals(i, "men's ring", StringComparison.OrdinalIgnoreCase))|(string.Equals(i, "men's rings", StringComparison.OrdinalIgnoreCase)))
                        {
                            Console.WriteLine($"push notification API for men's ring");
                            var interclient = new HttpClient();
                            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.interakt.ai/v1/public/message/");
                            request.Headers.Add("Authorization", "Basic TTNsNlFUWnZlZm1sNzFBeWZ3cGxJSWhaTU9KWktHX0VZSzZZMjMzdWlwbzo=");
                            var content = new StringContent($@"{{
                            ""countryCode"": ""{countryCode.Item1}"",
                            ""phoneNumber"": ""{countryCode.Item2}"",
                            ""callbackData"": ""Test for template"",
                            ""type"": ""Template"",
                            ""template"": {{
                                ""name"": ""gentsring"",
                                ""languageCode"": ""en"",
                                ""headerValues"": [
                                    ""https://interaktprodmediastorage.blob.core.windows.net/mediaprodstoragecontainer/dd2b315b-a1f6-4f2b-a3f7-f6a67718d9c9/message_template_media/z1aZSOVMeA1n/Gents%20Ring.pdf?se=2029-11-26T07%3A22%3A48Z&sp=rt&sv=2019-12-12&sr=b&sig=Yc6tGZ6ufyePq7YXeu1VTHtiT3KviPS1v8K8MljeOf4%3D""
                                ],
                                ""bodyValues"": [
                                    ""Anoop MR""
                                ]
                            }}
                        }}", null, "application/json");
                            request.Content = content;
                            var interresponse = await interclient.SendAsync(request);
                            interresponse.EnsureSuccessStatusCode();
                            Console.WriteLine(await interresponse.Content.ReadAsStringAsync());
                        }
                        else if((string.Equals(i, "ladies ring", StringComparison.OrdinalIgnoreCase))|(string.Equals(i, "ladies rings", StringComparison.OrdinalIgnoreCase)))
                        {
                            Console.WriteLine($"push notification API for ladies ring");
                            var interclient = new HttpClient();
                            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.interakt.ai/v1/public/message/");
                            request.Headers.Add("Authorization", "Basic TTNsNlFUWnZlZm1sNzFBeWZ3cGxJSWhaTU9KWktHX0VZSzZZMjMzdWlwbzo=");
                            var content = new StringContent($@"{{
                            ""countryCode"": ""{countryCode.Item1}"",
                            ""phoneNumber"": ""{countryCode.Item2}"",
                            ""callbackData"": ""Test for template"",
                            ""type"": ""Template"",
                            ""template"": {{
                                ""name"": ""ladies_ring"",
                                ""languageCode"": ""en"",
                                ""headerValues"": [
                                    ""https://interaktprodmediastorage.blob.core.windows.net/mediaprodstoragecontainer/dd2b315b-a1f6-4f2b-a3f7-f6a67718d9c9/message_template_media/OKM6nb0v6QMu/Ladies%20Ring.pdf?se=2029-11-24T06%3A45%3A30Z&sp=rt&sv=2019-12-12&sr=b&sig=DWLSppGZxeMqVcUgynEwMRJHMmO42lFGkl2BZ2S8VIA%3D""
                                ],
                                ""bodyValues"": [
                                    ""Anoop MR""
                                ]
                            }}
                        }}", null, "application/json");
                            request.Content = content;
                            var interresponse = await interclient.SendAsync(request);
                            interresponse.EnsureSuccessStatusCode();
                            Console.WriteLine(await interresponse.Content.ReadAsStringAsync());
                        }
                    }
                    else
                    {
                        Console.WriteLine("no API called");
                    }
                }
                // if (string.Equals(product_interested, "Bracelet", StringComparison.OrdinalIgnoreCase))
                // {
                //     var interclient = new HttpClient();
                //     var request = new HttpRequestMessage(HttpMethod.Post, "https://api.interakt.ai/v1/public/message/");
                //     request.Headers.Add("Authorization", "Basic TTNsNlFUWnZlZm1sNzFBeWZ3cGxJSWhaTU9KWktHX0VZSzZZMjMzdWlwbzo=");
                //     var content = new StringContent($@"{{
                //     ""countryCode"": ""{countryCode.Item1}"",
                //     ""phoneNumber"": ""{countryCode.Item2}"",
                //     ""callbackData"": ""Test for template"",
                //     ""type"": ""Template"",
                //     ""template"": {{
                //         ""name"": ""bracelet_lm"",
                //         ""languageCode"": ""en"",
                //         ""headerValues"": [
                //             ""https://interaktprodmediastorage.blob.core.windows.net/mediaprodstoragecontainer/dd2b315b-a1f6-4f2b-a3f7-f6a67718d9c9/message_template_media/6cPWliXZEJVn/Bracelet%2025-30%20gms.pdf?se=2029-11-24T07%3A03%3A29Z&sp=rt&sv=2019-12-12&sr=b&sig=kYqGp79FcOaAWukTt4KWiNE1XTRfdmwZtF8l/Y/bOXE%3D""
                //         ],
                //         ""bodyValues"": [
                //             ""Anoop MR""
                //         ]
                //     }}
                // }}", null, "application/json");
                //     request.Content = content;
                //     var interresponse = await interclient.SendAsync(request);
                //     interresponse.EnsureSuccessStatusCode();
                //     Console.WriteLine(await interresponse.Content.ReadAsStringAsync());
                // }
                // else if ((string.Equals(product_interested, "Rings", StringComparison.OrdinalIgnoreCase))||(string
                // .Equals(product_interested, "Ring", StringComparison.OrdinalIgnoreCase)))
                // {
                //     var interclient = new HttpClient();
                //     var request = new HttpRequestMessage(HttpMethod.Post, "https://api.interakt.ai/v1/public/message/");
                //     request.Headers.Add("Authorization", "Basic TTNsNlFUWnZlZm1sNzFBeWZ3cGxJSWhaTU9KWktHX0VZSzZZMjMzdWlwbzo=");
                //     var content = new StringContent($@"{{
                //     ""countryCode"": ""{countryCode.Item1}"",
                //     ""phoneNumber"": ""{countryCode.Item2}"",
                //     ""callbackData"": ""Test for template"",
                //     ""type"": ""Template"",
                //     ""template"": {{
                //         ""name"": ""ladies_ring"",
                //         ""languageCode"": ""en"",
                //         ""headerValues"": [
                //             ""https://interaktprodmediastorage.blob.core.windows.net/mediaprodstoragecontainer/dd2b315b-a1f6-4f2b-a3f7-f6a67718d9c9/message_template_media/OKM6nb0v6QMu/Ladies%20Ring.pdf?se=2029-11-24T06%3A45%3A30Z&sp=rt&sv=2019-12-12&sr=b&sig=DWLSppGZxeMqVcUgynEwMRJHMmO42lFGkl2BZ2S8VIA%3D""
                //         ],
                //         ""bodyValues"": [
                //             ""Anoop MR""
                //         ]
                //     }}
                // }}", null, "application/json");
                //     request.Content = content;
                //     var interresponse = await interclient.SendAsync(request);
                //     interresponse.EnsureSuccessStatusCode();
                //     Console.WriteLine(await interresponse.Content.ReadAsStringAsync());
                // }
        
                return $"We have collected the product details. We have send a product details to this Whatsapp Number.Thanks for your time. if their is anything you need to know about jewlex or our products feel free to ask me.";

            }


        }
        catch (JsonException ex)
        {
            Console.WriteLine("Error decoding JSON: " + ex.Message);
        }
    }
    else
    {
        Console.WriteLine("No JSON data found in the text.");
    }
    return response.Value.Choices[0].Message.Content;
}

bool IsOutOfContext(string response)
{
    // This is a placeholder logic for detecting out-of-context replies.
    // You can expand this using machine learning models or keyword detection.
    var outOfContextKeywords = new List<string> { "restaurant", "movie", "travel", "weather", "terroism","money laundering","Marianna legalisation", 
"Marijuana legislation","Marijuana"};

    return outOfContextKeywords.Any(keyword => response.Contains(keyword, StringComparison.OrdinalIgnoreCase));
}
// // Function to send a message to the user and recognize their response
async Task SayAndRecognize(CallMedia callConnectionMedia, PhoneNumberIdentifier phoneId, string response)
{
    Console.WriteLine($"printing res-->{response}");
    if (response.Length > 400)
    {
        var parts = SplitIntoChunks(response, 400);
        foreach (string part in parts)
        {
        var chatGPTResponseSource = new TextSource(part){
                VoiceName = "en-US-JennyMultilingualV2Neural"
        };
        var recognizeOptions =
        new CallMediaRecognizeSpeechOptions(
            targetParticipant: CommunicationIdentifier.FromRawId(phoneId.RawId))
        {
            Prompt = chatGPTResponseSource,
            EndSilenceTimeout = TimeSpan.FromMilliseconds(800)
        };

            var recognize_result = await callConnectionMedia.StartRecognizingAsync(recognizeOptions);
        }
    }
    // else if (response.all_details_collected == "true" )
    // {
    //     Console.WriteLine($"going to end the call");
    //     await callConnection.HangUpAsync(true);


    // }
    else{
        Console.WriteLine($"splitting chunks");
            // Set up the text source for the chatbot's response
    var chatGPTResponseSource = new TextSource(response) {
        VoiceName = "en-US-JennyMultilingualV2Neural"
    };
    // Recognize the user's speech after sending the chatbot's response
    var recognizeOptions =
        new CallMediaRecognizeSpeechOptions(
            targetParticipant: CommunicationIdentifier.FromRawId(phoneId.RawId))
        {
            Prompt = chatGPTResponseSource,
            EndSilenceTimeout = TimeSpan.FromMilliseconds(800)
        };
    var recognize_result = await callConnectionMedia.StartRecognizingAsync(recognizeOptions);

    }

 }
 List<string> SplitIntoChunks(string text, int chunkSize)
{
    var chunks = new List<string>();
    for (int i = 0; i < text.Length; i += chunkSize)
    {
        chunks.Add(text.Substring(i, Math.Min(chunkSize, text.Length - i)));
    }
    return chunks;
}
public class CallRequest
{
    public required string PhoneNumber { get; set; }
    public required string Prompt { get; set; }
    public string call_id { get; set; }
}
// public class CallIntelligenceOptions
// {
// public Uri CognitiveServicesEndpoint { get; set; }
// }

public class UserInfo
{
    public string? full_name { get; set; }
    public string? interested_for_credit_card { get; set; }
    public string? annual_income { get; set; }
    public string? employement_status { get; set; }
    public string? any_current_credit_card { get; set; }
    public string? preffered_credit_limit { get; set; }
}
