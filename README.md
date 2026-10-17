# HR Payroll – microservices

The single API (`../HrPayroll.Api`) split into 11 services, each with its **own SQL Server database**, talking to each
other through **Kafka**. An API gateway keeps the old addresses, so the web app and the mobile app work unchanged.

```
 Web app (4200) ─┐                       ┌─ Identity      :5101  HrPayroll_Identity
 Mobile app ─────┤                       ├─ Employees     :5102  HrPayroll_Employees
                 ▼                       ├─ Leave         :5103  HrPayroll_Leave
        API gateway (YARP)               ├─ Attendance    :5104  HrPayroll_Attendance
   https://localhost:7169  ───routes───► ├─ Payroll       :5105  HrPayroll_Payroll
   http://localhost:5032                 ├─ Expenses      :5106  HrPayroll_Expenses
   (+ composes dashboards, year-end)     ├─ Performance   :5107  HrPayroll_Performance
                                         ├─ Recruitment   :5108  HrPayroll_Recruitment
                                         ├─ Helpdesk      :5109  HrPayroll_Helpdesk
                                         ├─ Chat          :5110  HrPayroll_Chat   (SignalR /hubs/chat)
                                         └─ Notifications :5111  HrPayroll_Notifications
                          all services ◄──── Kafka (localhost:9092) ────►
```

## Services

| Service | URLs it answers | Owns (tables) | Publishes | Listens to |
|---|---|---|---|---|
| **Identity** | `/api/auth`, `/api/roles`, `/api/employees/{id}/reset-password` | Users, Roles, RolePermissions, RefreshTokens | `hr.users`, `hr.roles` | `hr.identity.login-requests` (create login), `hr.employees` (deactivate login) |
| **Employees** | `/api/employees`, `/api/departments`, `/api/company`, `/api/org`, `/api/holidays`, `/api/employee-uploads` | Employees, Departments, Holidays, CompanyProfile, EmployeePhotos, EmployeeUploads | `hr.employees`, `hr.departments`, `hr.holidays`, `hr.company`, `hr.identity.login-requests` | `hr.promotions` (apply promotion), `hr.salaries` (current pay on the list) |
| **Leave** | `/api/leaves`, `/api/year-end/leave-*` | LeaveTypes, LeaveBalances, LeaveRequests, LeaveYearRollovers | `hr.leave-requests` | `hr.employees` (allocate leave for a new employee) |
| **Attendance** | `/api/calendar`, `/api/attendance-locations`, `/api/employee-faces` | Attendance, WorkLogs, areas, phones, faces, selfies | – | `hr.leave-requests` (leave on calendars) |
| **Payroll** | `/api/salary`, `/api/payroll`, `/api/payslips`, `/api/documents`, `/api/promotions`, `/api/year-end/financial-years` | SalaryStructures, Payslips, EmployeeDocuments, Promotions, ClosedFinancialYears | `hr.salaries`, `hr.promotions` | `hr.leave-requests` (unpaid leave in payroll) |
| **Expenses** | `/api/expenses`, `/api/travel-claims` | office expenses, travel claims | – | – |
| **Performance** | `/api/appraisals`, `/api/feedback`, `/api/tasks` | appraisals, feedback, tasks | – | – |
| **Recruitment** | `/api/recruitment`, `/api/assessments`, `/api/public/tests` | jobs, candidates, interviews, question banks, tests | – | – |
| **Helpdesk** | `/api/it-requests`, `/api/visits` | IT requests, visits | – | – |
| **Chat** | `/api/chat`, `/hubs/chat` | conversations, messages, attachments | – | – |
| **Notifications** | `/api/emails`, `/api/email-settings` | EmailMessages, EmailSettings | – | `hr.notifications.emails` (log + send every email) |

Every service also listens to `hr.employees`, `hr.departments`, `hr.holidays`, `hr.company`, `hr.users` and `hr.roles`
to keep **its own read-only copy of the company directory**, so it can join employee names, managers and roles
locally. The copies never hold password hashes, and only Payroll keeps PAN / bank details.

The gateway answers three pages itself by calling several services in parallel with the caller's token:
`/api/dashboard/admin`, `/api/dashboard/employee` and `/api/year-end`. Service-to-service calls that need an answer
immediately use `/internal/...` endpoints with a shared key (never routed by the gateway). Today that is one call:
Attendance reads an employee's ID-card photo from Employees.

## How Kafka is used

* **Transactional outbox.** A service saves its change and the event in its own database, in one transaction
  (`OutboxMessages`). A background relay publishes the events to Kafka in order and marks them sent. If Kafka is down,
  the app keeps working and the events go out when Kafka is back.
* **Inbox.** Kafka delivers at least once. Each consumer records the messages it has handled (`InboxMessages`) and
  skips repeats.
* **Ordering.** Each message is keyed by the record's id, so all changes to one employee (or leave request…) arrive in order.
* **Compacted topics.** The directory, leave and salary topics keep the newest message per key forever. A service added
  later builds its copies by reading those topics from the beginning. When an owning service starts against an empty
  topic (for example a new Kafka), it publishes the current state of all its records once.
* **Dead letters.** A message that fails 5 times goes to `<topic>.dlq`, so it cannot block the topic.
* **Consistency.** Copies are eventually consistent, usually within a second. Example: a new employee's login exists
  in Identity a moment after `POST /api/employees` returns.

Kafka UI (with Docker): http://localhost:8085 shows topics, messages and consumer lag.

## Running

```powershell
# First time only: create the 11 databases and copy the data from HrPayrollDb (it is only read)
powershell -ExecutionPolicy Bypass -File database\setup-databases.ps1

# Start Kafka (Docker, or the local fallback), the services and the gateway
powershell -ExecutionPolicy Bypass -File run-all.ps1

# Stop the services (Kafka keeps running)
powershell -ExecutionPolicy Bypass -File stop-all.ps1
```

* Each service runs in a minimized window `HR <name>` and logs to `logs\<name>.log`.
  Swagger: `http://localhost:51xx/swagger`. Status of all services: `http://localhost:5032/health/services`.
* **Kafka.** `run-all.ps1` uses `deploy\docker-compose.yml` when Docker Desktop is running. Otherwise it uses the local
  Kafka in `%LOCALAPPDATA%\HrKafka` (`deploy\start-local-kafka.ps1`, needs Java 17).
* The old single API must not be running: the gateway uses its ports.

## Code layout

```
src/BuildingBlocks/HrPayroll.Contracts      API models (Dtos) + Kafka events and topic names
src/BuildingBlocks/HrPayroll.BuildingBlocks  JWT + permissions, errors, ServiceDbContext (outbox / inbox / directory copy),
                                             OutboxRelay, KafkaConsumerService, EmailQueue, AddServiceDefaults()
src/Services/HrPayroll.<Service>             one ASP.NET Core service each (Controllers, Services, Entities, Data)
src/Gateway/HrPayroll.Gateway                YARP routes (GatewayRoutes.cs) + composed pages (Composition.cs)
database/                                    generate-service-schemas.ps1, services\*.sql, migrate-from-single-database.sql
deploy/                                      docker-compose.yml (Kafka + Kafka UI), start-local-kafka.ps1
```

**Adding an event**
1. Add the record to `Contracts/Events/IntegrationEvents.cs`, plus a topic if it needs a new one.
2. Publish it from the owning service: `db.Publish(topic, key, evt)` before `SaveChanges`. For directory-style data,
   override `Publishes` / `EventsAfterSaveAsync` in the service's DbContext.
3. Handle it in the listening service: implement `IIntegrationEventHandler<T>` and register it with
   `builder.Services.AddEventHandler<T, Handler>(topic)`.

## Going back to the single API

Run `stop-all.ps1`, then start `HrPayroll.Api` as before. `HrPayrollDb` was not changed by the move. Changes made while
the microservices were running are in the `HrPayroll_*` databases only.

## Before production

Set real secrets through environment variables or a vault: `Jwt__SigningKey` (the same value in every service) and
`InternalApi__Key`. Also set up Kafka with TLS / SASL and several brokers, a CORS origin list on the gateway, and HTTPS
between the gateway and the services.
