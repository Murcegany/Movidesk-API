# Movidesk API - Ticket Integration

## Description
This project aims to integrate with the Movidesk API to obtain ticket information and its respective details, such as customers, actions, timesheets, expenses, attachments and much more.

The API offers ticket query functionality with various parameters and allows the use of collection expansions to return additional information, such as custom fields and action history.

## Main Endpoints

### 1. List Tickets

To list tickets, use the following endpoint:
GET: https://api.movidesk.com/public/v1/tickets?token=token_here&$select=id,type,origin,status

**Base URL**: `https://api.movidesk.com/public/v1`

**Important**: The API has a limit of 10 requests per minute.

**Attention**: The `/tickets` route brings tickets with an update date (`lastupdate`) less than 90 days ago. Tickets that have an older update date must be searched in the `/tickets/past` route.

**Request Example**:
GET: https://api.movidesk.com/public/v1/tickets?token=token_here&id=1

#### Getting a Ticket List
- **Parameters**: `token`, `$select`
- **Optional parameter**: `includeDeletedItems` (if true, will return actions, customers, parent tickets and child tickets that were associated with the ticket and deleted).

**Example**:
GET: https://api.movidesk.com/public/v1/tickets?token=token_here&$select=id,type,origin,status

### 2. Using `$expand` for Queries

Using the `$expand` parameter in your API URI allows you to query collections of data/objects within the API requests used.

**Example**:
GET: https://api.movidesk.com/public/v1/tickets?token=token_here&$select=id&$expand=customFieldValues


#### Requesting Data from Nested Collections

To expand the `items` collection inside the `customFieldValues` collection, you can nest one `$expand` inside the other:

**Example**:
GET: https://api.movidesk.com/public/v1/tickets?token=token_here&$select=id&$expand=customFieldValues($expand=items)


## Integration in C#
Integration in C# involves requesting a list of tickets array. It is necessary to integrate ID with ID, as some fields are not present in the ticket list, making it necessary to request the ID to obtain the corresponding data.

### Priority Field Analysis

It will not be necessary to take all the fields, as many are specific to Movidesk. Focus should be on fields that are relevant to the application.

### Configuring User Secrets

Change the Token
```bash
dotnet user-secrets set "AppSettings:Token" "token_here"
```

Change the Connection String
```bash
dotnet user-secrets set "AppSettings:ConnectionString" "string_here"
```

Check Changes
```bash
dotnet user-secrets list
```

Secret cleaning
```bash
dotnet user-secrets clear
```

### Flowchart

Retrieve IDs: Search all recent and past ticket IDs.
Batch Processing: Depending on your API limits, you can retrieve detailed information for each ticket individually or in batches (if the API supports batch requests).
Store in Database: After retrieving the necessary details for each ticket, insert or update the records in your database.

### Diagrama UML

```mermaid
classDiagram
    class Ticket {
        +int id(10)
        +string protocol(30)
        +int type(1)
        +string subject(350)
        +string category(128)
        +string urgency(128)
        +string status(128)
        +string baseStatus(128)
        +int justification(128)
        +int origin(1)
        +datetime createdDate
        +int originEmailAccount(128)
        +string ownerTeam(128)
        +string baseStatus
        +List serviceFull(1024)
        +int serviceFirstLevelId
        +string serviceFirstLevel(1024)
        +string serviceSecondLevel(1024)
        +string serviceThirdLevel(1024)
        +string contactForm(128)
        +array tags
        +string cc(1024)
        +datetime resolvedIn
        +datetime closedIn
        +datetime canceledIn
        +int actionCount
        +int lifeTimeWorkingTime
        +int stoppedTime
        +int stoppedTimeWorkingTime
        +bool resolvedInFirstCall
        +int chatWidget(128)
        +string chatGroup
        +int chatTalkTime
        +int chatWaitingTime
        +int sequence
        +string slaAgreement(128)
        +string slaAgreementRule(128)
        +int slaSolutionTime
        +int slaResponseTime
        +bool slaSolutionChangedByUser
        +person slaSolutionChangedBy
        +datetime  slaSolutionDate
        +bool  slaSolutionDateIsPaused
        +datetime  slaResponseDate
        +datetime  slaRealResponseDate
        +datetime  jiraIssueKey(64)
        +int  redmineIssueId
        +person  clients
        +actions  actions
        +parentTickets  parentTickets
        +childrenTickets  childrenTickets
        +ownerHistories  ownerHistories
        +statusHistories  statusHistories
        +satisfaciton satisfactionSurveyResponses 
        +customField customFieldValues 
        +assets  assets
        +statusHistories  statusHistories
        +person owner
        +person createdBy
   }

    class person {
        +int id (64)
        +int personType(1)
        +int profileType(1)
        +String businessName(128)
        +String email(128)
        +String phone(128)
        +bool isDeleted
        +person organization
    }
    
    class actions {
        +int id(10)
        +int type(1)
        +int origin(1)
        +String description(max)
        +String htmlDescription(max)
        +String status(128)
        +string justification(128)
        +datetime createdDate
        +person createdBy
        +bool isDeleted
        +appointments timeAppointments
        +expenses expenses
        +attachments attachments
        +array tags
    }
