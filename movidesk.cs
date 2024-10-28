using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Npgsql;
using Newtonsoft.Json.Linq;
using NpgsqlTypes;
using System.Data;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using System.ComponentModel.DataAnnotations;

public class MovideskApi
{

    private static string connectionString;
    private static string token; 
    private static HttpClient client = new HttpClient();
    private const string baseUrl = "https://api.movidesk.com/public/v1/tickets";

    public static async Task Main(string[] args)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddUserSecrets<MovideskApi>();

        var configuration = builder.Build();

        token = configuration["AppSettings:Token"]; 
            string connectionString = configuration["AppSettings:ConnectionString"];

        await FetchAndInsertTicketsAsync(token, connectionString);
    }

    public static async Task FetchAndInsertTicketsAsync(string token, string connectionString)
    {
        const int maxRetries = 3;
        const int delayBetweenRetriesSeconds = 2;

        try
        {
            List<string> allTicketIds = new List<string>();

            // normal requests
            var normalTicketsResponse = await SendAsync($"{baseUrl}?token={token}&$select=id", null, "GET", "application/json");
            allTicketIds.AddRange(ExtractIds(normalTicketsResponse));

            // past tickets requests
            var pastTicketsResponse = await SendAsync($"{baseUrl}/past?token={token}&$select=id", null, "GET", "application/json");
            allTicketIds.AddRange(ExtractIds(pastTicketsResponse));

            Console.WriteLine($"Total IDs found: {allTicketIds.Count}");

            using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                List<string> existingTicketIds = await GetExistingTicketIdsAsync(connection);

                allTicketIds = allTicketIds.Except(existingTicketIds).ToList();
                Console.WriteLine($"Total IDs to be inserted: {allTicketIds.Count}");

                string filePath = "ticket_ids.txt";
                await SaveIdsToFile(allTicketIds, filePath);
                Console.WriteLine($"IDs saved in: {filePath}");

                Console.WriteLine("Database connection established.");

                int requestCount = 0;

                foreach (var ticketId in allTicketIds.ToList())
                {
                    if (requestCount >= 10)
                    {
                        Console.WriteLine("Request limit reached. Waiting 1 minute...");
                        await Task.Delay(TimeSpan.FromMinutes(1));
                        requestCount = 0;
                    }

                    Ticket ticket = await GetTicketDetailsAsync(ticketId);
                    if (ticket != null)
                    {
                        Console.WriteLine($"Inserting ticket with ID: {ticket.Id}");

                        await InsertTicketAsync(connection, ticket);
                        Console.WriteLine($"Ticket with ID: {ticket.Id} successfully inserted.");

                        // owner
                        if (ticket.Owner != null)
                        {
                            await InsertOwnerAsync(connection, ticket.Owner);
                            Console.WriteLine($"Owner with ID: {ticket.Owner.Id} successfully inserted.");
                        }
                        else
                        {
                            Console.WriteLine($"No Owner found for Ticket ID: {ticket.Id}");
                        }

                        // createdBy 
                        if (ticket.CreatedBy != null)
                        {
                            await InsertCreatedByAsync(connection, ticket.CreatedBy);
                            Console.WriteLine($"CreatedBy with ID: {ticket.CreatedBy.Id} successfully inserted.");
                        }
                        else
                        {
                            Console.WriteLine($"No CreatedBy found for Ticket ID: {ticket.Id}");
                        }

                        // slaSolutionChangedBy 
                        if (ticket.SlaSolutionChangedBy != null)
                        {
                            await InsertSlaSolutionChangedByAsync(connection, ticket.SlaSolutionChangedBy);
                            Console.WriteLine($"SlaSolutionChangedBy with ID: {ticket.SlaSolutionChangedBy.Id} successfully inserted.");
                        }
                        else
                        {
                            Console.WriteLine($"No SlaSolutionChangedBy found for Ticket ID: {ticket.Id}");
                        }

                        // actions
                        if (ticket.Actions != null && ticket.Actions.Count > 0)
                        {
                            foreach (var action in ticket.Actions)
                            {
                                await InsertActionsAsync(connection, action, ticket);
                                Console.WriteLine($"Action with ID: {action.Id} successfully inserted.");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"No Actions found for Ticket ID: {ticket.Id}");
                        }

                        // clients
                        if (ticket.Clients != null && ticket.Clients.Count > 0)
                        {
                            await InsertClientsAsync(connection, ticket.Clients);
                            Console.WriteLine($"Clients successfully inserted for Ticket ID: {ticket.Id}.");
                        }
                        else
                        {
                            Console.WriteLine($"No Clients found for Ticket ID: {ticket.Id}");
                        }

                        Console.WriteLine($"Ticket with ID: {ticket.Id} successfully inserted.");

                        await RemoveIdFromFileAsync(filePath, ticketId);
                    }
                    else
                    {
                        Console.WriteLine($"No details found for ticket with ID: {ticketId}");
                    }

                    Console.WriteLine("Ticket processing completed.");
                    requestCount++;
                }
            }
        }
        catch (NpgsqlException ex)
        {
            Console.WriteLine($"Database connection error: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"HTTP request error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An unexpected error occurred: {ex.Message}");
        }
    }

    private static async Task RemoveIdFromFileAsync(string filePath, string ticketId)
    {
        var lines = await File.ReadAllLinesAsync(filePath);
        var updatedLines = lines.Where(line => line != ticketId).ToArray();
        await File.WriteAllLinesAsync(filePath, updatedLines);
        Console.WriteLine($"ID {ticketId} removed from file {filePath}.");
    }

    private static async Task<List<string>> GetExistingTicketIdsAsync(NpgsqlConnection connection)
    {
        List<string> existingTicketIds = new List<string>();
        const string query = "SELECT Id FROM Tickets";

        using (var command = new NpgsqlCommand(query, connection))
        using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                existingTicketIds.Add(reader.GetString(0)); 
            }
        }

        return existingTicketIds;
    }

    public static async Task<string> SendAsync(string uri, string content, string method, string contentType)
    {
        var req = WebRequest.Create(uri);
        req.ContentType = contentType;
        req.Method = method;

        if (method != "GET" && content != null)
        {
            using (var stream = await req.GetRequestStreamAsync())
            using (var streamWriter = new StreamWriter(stream))
            {
                await streamWriter.WriteAsync(content);
            }
        }

        var httpResponse = (HttpWebResponse)await req.GetResponseAsync();
        using (var stream = httpResponse.GetResponseStream())
        {
            if (stream == null)
                return null;

            using (var streamReader = new StreamReader(stream))
            {
                return await streamReader.ReadToEndAsync();
            }
        }
    }

    private static List<string> ExtractIds(string jsonResponse)
    {
        var ids = new List<string>();
        try
        {
            var ticketData = JsonConvert.DeserializeObject<List<dynamic>>(jsonResponse);
            foreach (var ticket in ticketData)
            {
                ids.Add(ticket.id.ToString());
            }
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            Console.WriteLine("Error processing the JSON response: " + ex.Message);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Unexpected error while extracting IDs: " + ex.Message);
        }

        return ids;
    }

    private static async Task SaveIdsToFile(List<string> ticketIds, string filePath)
    {
        try
        {
            using (StreamWriter writer = new StreamWriter(filePath))
            {
                foreach (var id in ticketIds)
                {
                    await writer.WriteLineAsync(id);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error saving IDs to file: " + ex.Message);
        }
    }

    private static async Task InsertTicketAsync(NpgsqlConnection connection, Ticket ticket)
    {
        if (string.IsNullOrEmpty(ticket.Id))
        {
            Console.WriteLine("Error: Ticket Id cannot be empty.");
            return;
        }

        const string query = @"
        INSERT INTO tickets (
            id, protocol, type, subject, category, urgency, status, basestatus, justification, origin, 
            createddate, originemailaccount, owner, ownerteam, createdby, servicefirstlevelid, 
            servicefirstlevel, servicesecondlevel, servicethirdlevel, contactform, cc, resolvedin, 
            reopenedin, closedin, lastactiondate, actioncount, lastupdate, lifetimeworkingtime, 
            stoppedtime, stoppedtimeworkingtime, resolvedinfirstcall, chatwidget, chatgroup, 
            chattalktime, chatwaitingtime, slaagreement, slaagreementrule, 
            slasolutiontime, slaresponsetime, slasolutionchangedbyuser, slasolutionchangedby, 
            slasolutiondate, slasolutiondateispaused, slaresponsedate, slarealresponsedate, clients
        ) 
        VALUES (
            @id, @protocol, @type, @subject, @category, @urgency, @status, @basestatus, @justification, @origin, 
            @createddate, @originemailaccount, @owner, @ownerteam, @createdby, @servicefirstlevelid, 
            @servicefirstlevel, @servicesecondlevel, @servicethirdlevel, @contactform, @cc, @resolvedin, 
            @reopenedin, @closedin, @lastactiondate, @actioncount, @lastupdate, @lifetimeworkingtime, 
            @stoppedtime, @stoppedtimeworkingtime, @resolvedinfirstcall, @chatwidget, @chatgroup, 
            @chattalktime, @chatwaitingtime, @slaagreement, @slaagreementrule, 
            @slasolutiontime, @slaresponsetime, @slasolutionchangedbyuser, @slasolutionchangedby, 
            @slasolutiondate, @slasolutiondateispaused, @slaresponsedate, @slarealresponsedate, @clients
        )
        ON CONFLICT (id) DO NOTHING
        ";

        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            using (var cmd = new NpgsqlCommand(query, connection, transaction))
            {
                cmd.Parameters.AddWithValue("id", ticket.Id);
                cmd.Parameters.AddWithValue("protocol", ticket.Protocol ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("type", ticket.Type.HasValue ? (object)ticket.Type.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("subject", ticket.Subject);
                cmd.Parameters.AddWithValue("category", ticket.Category ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("urgency", ticket.Urgency ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("status", ticket.Status);
                cmd.Parameters.AddWithValue("basestatus", ticket.BaseStatus);
                cmd.Parameters.AddWithValue("justification", ticket.Justification ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("origin", ticket.Origin);
                cmd.Parameters.AddWithValue("createddate", ticket.CreatedDate);
                cmd.Parameters.AddWithValue("originemailaccount", ticket.OriginEmailAccount ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("owner", ticket.Owner != null ? ticket.Owner.Id : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("ownerteam", ticket.OwnerTeam ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("createdby", ticket.CreatedBy != null ? ticket.CreatedBy.Id : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("servicefirstlevelid", ticket.ServiceFirstLevelId.HasValue ? (object)ticket.ServiceFirstLevelId.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("servicefirstlevel", ticket.ServiceFirstLevel ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("servicesecondlevel", ticket.ServiceSecondLevel ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("servicethirdlevel", ticket.ServiceThirdLevel ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("contactform", ticket.ContactForm ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("cc", ticket.Cc ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("resolvedin", ticket.ResolvedIn.HasValue ? (object)ticket.ResolvedIn.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("reopenedin", ticket.ReopenedIn.HasValue ? (object)ticket.ReopenedIn.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("closedin", ticket.ClosedIn.HasValue ? (object)ticket.ClosedIn.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("lastactiondate", ticket.LastActionDate);
                cmd.Parameters.AddWithValue("actioncount", ticket.ActionCount);
                cmd.Parameters.AddWithValue("lastupdate", ticket.LastUpdate);
                cmd.Parameters.AddWithValue("lifetimeworkingtime", ticket.LifetimeWorkingTime.HasValue ? (object)ticket.LifetimeWorkingTime.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("stoppedtime", ticket.StoppedTime.HasValue && ticket.StoppedTime >= 0 ? (object)ticket.StoppedTime.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("stoppedtimeworkingtime", ticket.StoppedTimeWorkingTime.HasValue ? (object)ticket.StoppedTimeWorkingTime.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("resolvedinfirstcall", ticket.ResolvedInFirstCall);
                cmd.Parameters.AddWithValue("chatwidget", ticket.ChatWidget ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("chatgroup", ticket.ChatGroup ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("chattalktime", ticket.ChatTalkTime.HasValue ? (object)ticket.ChatTalkTime.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("chatwaitingtime", ticket.ChatWaitingTime.HasValue ? (object)ticket.ChatWaitingTime.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("slaagreement", ticket.SlaAgreement ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("slaagreementrule", ticket.SlaAgreementRule ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("slasolutiontime", ticket.SlaSolutionTime.HasValue ? (object)ticket.SlaSolutionTime.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("slaresponsetime", ticket.SlaResponseTime.HasValue ? (object)ticket.SlaResponseTime.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("slasolutionchangedbyuser", ticket.SlaSolutionChangedByUser ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("slasolutionchangedby", ticket.SlaSolutionChangedBy != null ? ticket.SlaSolutionChangedBy.Id : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("slasolutiondate", ticket.SlaSolutionDate ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("slasolutiondateispaused", ticket.SlaSolutionDateIsPaused);
                cmd.Parameters.AddWithValue("slaresponsedate", ticket.SlaResponseDate ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("slarealresponsedate", ticket.SlaRealResponseDate ?? (object)DBNull.Value);
                
                cmd.Parameters.AddWithValue("actions", System.Text.Json.JsonSerializer.Serialize(ticket.Actions));
                // check if the clients list is not null and contains at least one client
                if (ticket.Clients != null && ticket.Clients.Count > 0)
                {
                    // add the ID of the first client
                    cmd.Parameters.AddWithValue("clients", ticket.Clients[0].Id);
                }
                else
                {
                    // if the list is null or empty, add DBNull
                    cmd.Parameters.AddWithValue("clients", (object)DBNull.Value);
                }

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        Console.WriteLine($"Ticket with ID {ticket.Id} inserted/updated successfully.");
                    }
                }          
            }

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            Console.WriteLine($"Error inserting or updating ticket: {ex.Message}");
        }
    }


    private static async Task<Ticket> GetTicketDetailsAsync(string ticketId)
    {
        Console.WriteLine($"Fetching details for Ticket ID: {ticketId}");
        var response = await client.GetAsync($"{baseUrl}?token={token}&id={ticketId}");
        response.EnsureSuccessStatusCode();
        var jsonResponse = await response.Content.ReadAsStringAsync();
        var ticketData = JObject.Parse(jsonResponse);

        var owner = ticketData["owner"]?.ToObject<Owner>();
        var ticket = ticketData.ToObject<Ticket>();
        ticket.Owner = owner;

        return ticket;
    }

    public static async Task InsertActionsAsync(NpgsqlConnection connection, Actions actions, Ticket ticket)
    {
        // check if the action already exists for the ticket
        string checkQuery = @"
            SELECT COUNT(*) 
            FROM table_actions 
            WHERE Id = @Id AND Id_Ticket = @Id_Ticket";

        using (var checkCmd = new NpgsqlCommand(checkQuery, connection))
        {
            checkCmd.Parameters.AddWithValue("Id", actions.Id);
            checkCmd.Parameters.AddWithValue("Id_Ticket", ticket != null && !string.IsNullOrEmpty(ticket.Id) ? ticket.Id : (object)DBNull.Value);

            int count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
            
            if (count > 0)
            {
                Console.WriteLine($"Action with Id {actions.Id} already exists for Ticket Id {ticket.Id}. Ignoring insertion.");
                return; // exit the method if the action already exists
            }
        }

        string sqlQuery = @"
            INSERT INTO table_actions (Id, Type, Origin, Description, HtmlDescription, Status, Justification, CreatedDate, CreatedBy, IsDeleted, Id_Ticket) 
            VALUES (@Id, @Type, @Origin, @Description, @HtmlDescription, @Status, @Justification, @CreatedDate, @CreatedBy, @IsDeleted, @Id_Ticket)";

        using (var cmd = new NpgsqlCommand(sqlQuery, connection))
        {
            cmd.Parameters.AddWithValue("Id", actions.Id);
            cmd.Parameters.AddWithValue("Type", actions.Type);
            cmd.Parameters.AddWithValue("Origin", actions.Origin);
            cmd.Parameters.AddWithValue("Description", actions.Description ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("HtmlDescription", actions.HtmlDescription ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("Status", actions.Status ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("Justification", actions.Justification ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("CreatedDate", actions.CreatedDate.HasValue ? (object)actions.CreatedDate.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("CreatedBy", actions.CreatedBy?.Id ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("IsDeleted", actions.IsDeleted);
            
            cmd.Parameters.AddWithValue("Id_Ticket", ticket != null && !string.IsNullOrEmpty(ticket.Id) ? ticket.Id : (object)DBNull.Value);

            try
            {
                await cmd.ExecuteNonQueryAsync();
                Console.WriteLine("Action inserted/updated successfully.");
            }
            catch (NpgsqlException ex) when (ex.SqlState == "23503") // error code for foreign key violation
            {
                Console.WriteLine($"Error inserting action: {ex.Message}. Ignoring the action.");
            }
        }
    }

    public static async Task InsertOwnerAsync(NpgsqlConnection connection, Owner owner)
    {
        if (owner == null)
        {
            Console.WriteLine("Owner cannot be null.");
            return;
        }

        using (var cmd = new NpgsqlCommand(@"INSERT INTO table_owner (id, businessname, email, phone, persontype, profiletype, isdeleted, organizationid) 
            SELECT src.id, src.businessname, src.email, src.phone, src.persontype, src.profiletype, src.isdeleted, src.organizationid 
            FROM owner AS src 
            ON CONFLICT (id) 
            DO UPDATE SET 
                businessname = EXCLUDED.businessname, 
                email = EXCLUDED.email, 
                phone = EXCLUDED.phone, 
                persontype = EXCLUDED.persontype, 
                profiletype = EXCLUDED.profiletype, 
                isdeleted = EXCLUDED.isdeleted, 
                organizationid = EXCLUDED.organizationid", connection))
        {
            cmd.Parameters.AddWithValue("Id", owner.Id);
            cmd.Parameters.AddWithValue("BusinessName", owner.BusinessName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("Email", owner.Email ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("Phone", owner.Phone ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("PersonType", owner.PersonType);
            cmd.Parameters.AddWithValue("ProfileType", owner.ProfileType);
            cmd.Parameters.AddWithValue("IsDeleted", owner.IsDeleted);
            cmd.Parameters.AddWithValue("OrganizationId", owner.Organization?.Id ?? (object)DBNull.Value);

            await cmd.ExecuteNonQueryAsync();

            Console.WriteLine("Owner inserted/updated:");
        }
    }

    public static async Task InsertCreatedByAsync(NpgsqlConnection connection, CreatedBy createdBy)
    {
        if (createdBy == null)
        {
            Console.WriteLine("CreatedBy cannot be null.");
            return;
        }

        using (var cmd = new NpgsqlCommand(@"INSERT INTO table_owner (id, businessname, email, phone, persontype, profiletype, isdeleted, organizationid) 
            SELECT src.id, src.businessname, src.email, src.phone, src.persontype, src.profiletype, src.isdeleted, src.organizationid 
            FROM owner AS src 
            ON CONFLICT (id) 
            DO UPDATE SET 
                businessname = EXCLUDED.businessname, 
                email = EXCLUDED.email, 
                phone = EXCLUDED.phone, 
                persontype = EXCLUDED.persontype, 
                profiletype = EXCLUDED.profiletype, 
                isdeleted = EXCLUDED.isdeleted, 
                organizationid = EXCLUDED.organizationid", connection))
        {
            cmd.Parameters.AddWithValue("Id", createdBy.Id);
            cmd.Parameters.AddWithValue("BusinessName", createdBy.BusinessName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("Email", createdBy.Email ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("Phone", createdBy.Phone ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("PersonType", createdBy.PersonType);
            cmd.Parameters.AddWithValue("ProfileType", createdBy.ProfileType);
            cmd.Parameters.AddWithValue("IsDeleted", createdBy.IsDeleted);
            cmd.Parameters.AddWithValue("OrganizationId", createdBy.Organization?.Id ?? (object)DBNull.Value);

            await cmd.ExecuteNonQueryAsync();

            Console.WriteLine("Owner inserted/updated:");
        }
    }

    public static async Task InsertSlaSolutionChangedByAsync(NpgsqlConnection connection, SlaSolutionChangedBy slaSolutionChangedBy)
    {
        if (slaSolutionChangedBy == null)
        {
            Console.WriteLine("slaSolutionChangedBy cannot be null.");
            return;
        }

        using (var cmd = new NpgsqlCommand(@"INSERT INTO table_owner (id, businessname, email, phone, persontype, profiletype, isdeleted, organizationid) 
            SELECT src.id, src.businessname, src.email, src.phone, src.persontype, src.profiletype, src.isdeleted, src.organizationid 
            FROM owner AS src 
            ON CONFLICT (id) 
            DO UPDATE SET 
                businessname = EXCLUDED.businessname, 
                email = EXCLUDED.email, 
                phone = EXCLUDED.phone, 
                persontype = EXCLUDED.persontype, 
                profiletype = EXCLUDED.profiletype, 
                isdeleted = EXCLUDED.isdeleted, 
                organizationid = EXCLUDED.organizationid", connection))
        {
            cmd.Parameters.AddWithValue("Id", slaSolutionChangedBy.Id);
            cmd.Parameters.AddWithValue("BusinessName", slaSolutionChangedBy.BusinessName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("Email", slaSolutionChangedBy.Email ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("Phone", slaSolutionChangedBy.Phone ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("PersonType", slaSolutionChangedBy.PersonType);
            cmd.Parameters.AddWithValue("ProfileType", slaSolutionChangedBy.ProfileType);
            cmd.Parameters.AddWithValue("IsDeleted", slaSolutionChangedBy.IsDeleted);
            cmd.Parameters.AddWithValue("OrganizationId", slaSolutionChangedBy.Organization?.Id ?? (object)DBNull.Value);

            await cmd.ExecuteNonQueryAsync();

            Console.WriteLine("SlaSolutionChangedBy inserted/updated:");
        }
    }

    public static async Task InsertClientsAsync(NpgsqlConnection connection, List<Clients> clients)
    {
        if (clients == null || !clients.Any())
        {
            Console.WriteLine("Clients cannot be null or empty.");
            return;
        }

        foreach (var client in clients)
        {
            if (client == null)
            {
                Console.WriteLine("A client in the list cannot be null.");
                continue;
            }

            using (var transaction = await connection.BeginTransactionAsync())
            {
                try
                {
                    // Organization
                    if (client.Organization != null)
                    {
                        using (var orgCmd = new NpgsqlCommand(@"INSERT INTO table_owner (id, businessname, email, phone, persontype, profiletype) 
                            VALUES (@Id, @BusinessName, @Email, @Phone, @PersonType, @ProfileType) 
                            ON CONFLICT (id) 
                            DO UPDATE SET 
                                businessname = EXCLUDED.businessname, 
                                email = EXCLUDED.email, 
                                phone = EXCLUDED.phone, 
                                persontype = EXCLUDED.persontype, 
                                profiletype = EXCLUDED.profiletype", connection, transaction)) 
                        {
                            orgCmd.Parameters.AddWithValue("Id", client.Organization.Id);
                            orgCmd.Parameters.AddWithValue("BusinessName", client.Organization.BusinessName ?? (object)DBNull.Value);
                            orgCmd.Parameters.AddWithValue("Email", client.Organization.Email ?? (object)DBNull.Value);
                            orgCmd.Parameters.AddWithValue("Phone", client.Organization.Phone ?? (object)DBNull.Value);
                            orgCmd.Parameters.AddWithValue("PersonType", client.Organization.PersonType);
                            orgCmd.Parameters.AddWithValue("ProfileType", client.Organization.ProfileType);

                            await orgCmd.ExecuteNonQueryAsync();
                            Console.WriteLine($"Organization named {client.Organization.BusinessName} inserted/updated successfully.");
                        }
                    }

                    // Client
                    using (var clientCmd = new NpgsqlCommand(@"INSERT INTO table_owner (id, businessname, email, phone, persontype, profiletype, isdeleted, organizationid) 
                        VALUES (@Id, @BusinessName, @Email, @Phone, @PersonType, @ProfileType, @IsDeleted, @OrganizationId) 
                        ON CONFLICT (id) 
                        DO UPDATE SET 
                            businessname = EXCLUDED.businessname, 
                            email = EXCLUDED.email, 
                            phone = EXCLUDED.phone, 
                            persontype = EXCLUDED.persontype, 
                            profiletype = EXCLUDED.profiletype, 
                            isdeleted = EXCLUDED.isdeleted, 
                            organizationid = EXCLUDED.organizationid", connection, transaction)) 
                    {
                        clientCmd.Parameters.AddWithValue("Id", client.Id);
                        clientCmd.Parameters.AddWithValue("BusinessName", client.BusinessName ?? (object)DBNull.Value);
                        clientCmd.Parameters.AddWithValue("Email", client.Email ?? (object)DBNull.Value);
                        clientCmd.Parameters.AddWithValue("Phone", client.Phone ?? (object)DBNull.Value);
                        clientCmd.Parameters.AddWithValue("PersonType", client.PersonType);
                        clientCmd.Parameters.AddWithValue("ProfileType", client.ProfileType);
                        clientCmd.Parameters.AddWithValue("IsDeleted", client.IsDeleted);
                        clientCmd.Parameters.AddWithValue("OrganizationId", client.Organization?.Id ?? (object)DBNull.Value);

                        await clientCmd.ExecuteNonQueryAsync();
                        Console.WriteLine($"Client with organization ID {client.Organization?.Id} inserted/updated successfully.");
                    }

                    await transaction.CommitAsync();
                    Console.WriteLine("Client and Organization inserted/updated.");
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine($"Error inserting client/organization: {ex.Message}");
                }
            }
        }
    }

    public class Ticket
    {
        public string Id { get; set; }
        public string Protocol { get; set; }
        public int? Type { get; set; }
        public string Subject { get; set; }
        public string Category { get; set; }
        public string Urgency { get; set; }
        public string Status { get; set; }
        public string BaseStatus { get; set; }
        public string Justification { get; set; }
        public int Origin { get; set; }
        public DateTime CreatedDate { get; set; }
        public string OriginEmailAccount { get; set; }
        public Owner Owner { get; set; }
        public string OwnerTeam { get; set; }
        public CreatedBy CreatedBy { get; set; }
        public int? ServiceFirstLevelId { get; set; }
        public string ServiceFirstLevel { get; set; }
        public string ServiceSecondLevel { get; set; }
        public string ServiceThirdLevel { get; set; }
        public string ContactForm { get; set; }
        public string Cc { get; set; }
        public DateTime? ResolvedIn { get; set; }
        public DateTime? ReopenedIn { get; set; }
        public DateTime? ClosedIn { get; set; }
        public DateTime LastActionDate { get; set; }
        public int ActionCount { get; set; }
        public DateTime LastUpdate { get; set; }
        public int? LifetimeWorkingTime { get; set; }
        public int? StoppedTime { get; set; }
        public int? StoppedTimeWorkingTime { get; set; }
        public bool ResolvedInFirstCall { get; set; }
        public string ChatWidget { get; set; }
        public string ChatGroup { get; set; }
        public int? ChatTalkTime { get; set; }
        public int? ChatWaitingTime { get; set; }
        public string SlaAgreement { get; set; }
        public string SlaAgreementRule { get; set; }
        public int? SlaSolutionTime { get; set; }
        public int? SlaResponseTime { get; set; }
        public bool? SlaSolutionChangedByUser { get; set; }
        public SlaSolutionChangedBy SlaSolutionChangedBy { get; set; }
        public List<Clients> Clients { get; set; } = new List<Clients>();
        public DateTime? SlaSolutionDate { get; set; }
        public bool SlaSolutionDateIsPaused { get; set; }
        public DateTime? SlaResponseDate { get; set; }
        public DateTime? SlaRealResponseDate { get; set; }
        public List<Actions> Actions { get; set; } = new List<Actions>();
    }

    public class Owner
    {
        [StringLength(64)]
        public string Id { get; set; }

        [StringLength(128)]
        public string BusinessName { get; set; }

        [EmailAddress]
        [StringLength(128)]
        public string Email { get; set; }

        [StringLength(128)]
        public string Phone { get; set; }

        [Range(0, 1)]
        public int PersonType { get; set; }

        [Range(0, 1)]
        public int ProfileType { get; set; }

        public bool IsDeleted { get; set; }

        public Owner? Organization { get; set; }

        public string Id_Ticket { get; set; }
    }

    public class CreatedBy
    {
        public string Id { get; set; } // Size 64
        public string BusinessName { get; set; } // Size 128
        public string Email { get; set; } // Size 128
        public string Phone { get; set; } // Size 128
        public int PersonType { get; set; } // Size 1
        public int ProfileType { get; set; } // Size 1
        public bool IsDeleted { get; set; }
        
        // organization can be null if the creator is not associated with an organization
        public Owner? Organization { get; set; } // Allows for potential recursive relationships
        public string Id_Ticket { get; set; } // Ticket ID associated with the creator
    }

    public class SlaSolutionChangedBy
    {
        [StringLength(64)]
        public string Id { get; set; }

        [StringLength(128)]
        public string BusinessName { get; set; }

        [EmailAddress]
        [StringLength(128)]
        public string Email { get; set; }

        [StringLength(128)]
        public string Phone { get; set; }

        [Range(0, 1)]
        public int PersonType { get; set; }

        [Range(0, 1)]
        public int ProfileType { get; set; }

        public bool IsDeleted { get; set; }

        public Owner? Organization { get; set; }

        public string Id_Ticket { get; set; } // Ticket ID associated with the change
    }

    public class Organization
    {
        [StringLength(64)]
        public string Id { get; set; } // Size 64

        [StringLength(128)]
        public string BusinessName { get; set; } // Size 128

        [EmailAddress]
        [StringLength(128)]
        public string Email { get; set; } // Size 128

        [StringLength(128)]
        public string Phone { get; set; } // Size 128

        [Range(0, 1)]
        public int PersonType { get; set; } // Size 1

        [Range(0, 1)]
        public int ProfileType { get; set; } // Size 1

        public bool IsDeleted { get; set; }
    }

    public class Clients
    {
        [StringLength(64)]
        public string Id { get; set; } // Size 64

        [StringLength(128)]
        public string BusinessName { get; set; } // Size 128

        [EmailAddress]
        [StringLength(128)]
        public string Email { get; set; } // Size 128

        [StringLength(128)]
        public string Phone { get; set; } // Size 128

        [Range(0, 1)]
        public int PersonType { get; set; } // Size 1

        [Range(0, 1)]
        public int ProfileType { get; set; } // Size 1

        public bool IsDeleted { get; set; }

        public Organization Organization { get; set; } // Relationship to Organization

        [StringLength(64)]
        public string Id_Ticket { get; set; } // Size 64
    }

    public class Actions
    {
        public int Id { get; set; } // Action ID
        public int Type { get; set; } // Action type
        public int Origin { get; set; } // Action origin
        public string Description { get; set; } // Action description
        public string HtmlDescription { get; set; } // HTML description
        public string Status { get; set; } // Action status
        public string Justification { get; set; } // Justification
        public DateTime? CreatedDate { get; set; } // Creation date
        public Owner CreatedBy { get; set; } // Owner of the action
        public bool IsDeleted { get; set; } // Deletion status
        public Ticket Ticket { get; set; } // Associated ticket

        public string Id_Ticket => Ticket?.Id; // Derive Ticket ID
    }

}

