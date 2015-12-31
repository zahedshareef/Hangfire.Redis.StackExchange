﻿// Copyright © 2013-2015 Sergey Odinokov, Marco Casamento 
// This software is based on https://github.com/HangfireIO/Hangfire.Redis 

// Hangfire.Redis.StackExchange is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire.Redis.StackExchange is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire.Redis.StackExchange. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Hangfire.Annotations;
using StackExchange.Redis;
using System.Threading.Tasks;

namespace Hangfire.Redis
{
    internal class RedisMonitoringApi : IMonitoringApi
    {
        private readonly IDatabase _database;

		public RedisMonitoringApi([NotNull] IDatabase database)
        {
			if (database == null) throw new ArgumentNullException("database");

			_database = database;
        }

        public long ScheduledCount()
        {
            return UseConnection(redis => 
                redis.SortedSetLength(RedisStorage.Prefix+"schedule"));
        }

        public long EnqueuedCount(string queue)
        {
            return UseConnection(redis => 
                redis.ListLength(String.Format(RedisStorage.Prefix + "queue:{0}", queue)));
        }

        public long FetchedCount(string queue)
        {
            return UseConnection(redis => 
                redis.ListLength(String.Format(RedisStorage.Prefix + "queue:{0}:dequeued", queue)));
        }

        public long FailedCount()
        {
            return UseConnection(redis => redis.SortedSetLength(RedisStorage.Prefix + "failed"));
        }

        public long ProcessingCount()
        {
            return UseConnection(redis => redis.SortedSetLength(RedisStorage.Prefix + "processing"));
        }

        public long DeletedListCount()
        {
            return UseConnection(redis => redis.ListLength(RedisStorage.Prefix + "deleted"));
        }

        public JobList<ProcessingJobDto> ProcessingJobs(
            int from, int count)
        {
            return UseConnection(redis =>
            {
                var jobIds = redis.SortedSetRangeByRank(
                    RedisStorage.Prefix + "processing",
                    from,
                    from + count - 1).ToStringArray();

                return new JobList<ProcessingJobDto>(GetJobsWithProperties(redis,
                    jobIds,
                    null,
                    new[] { "StartedAt", "ServerName", "ServerId", "State" },
                    (job, jobData, state) => new ProcessingJobDto
                    {
                        ServerId = state[2] ?? state[1],
                        Job = job,
                        StartedAt = JobHelper.DeserializeNullableDateTime(state[0]),
                        InProcessingState = ProcessingState.StateName.Equals(
                            state[3], StringComparison.OrdinalIgnoreCase),
                    })
					.Where(x=> x.Value.ServerId != null)
					.OrderBy(x => x.Value.StartedAt).ToList());
            });
        }

        public JobList<ScheduledJobDto> ScheduledJobs(int from, int count)
        {
            return UseConnection(redis =>
            {
                var scheduledJobs = redis.SortedSetRangeByRankWithScores(
                    RedisStorage.Prefix + "schedule",
                    from,
                    from + count - 1).ToList();

                if (scheduledJobs.Count == 0)
                {
                    return new JobList<ScheduledJobDto>(new List<KeyValuePair<string, ScheduledJobDto>>());
                }

                var jobs = new Dictionary<string, List<string>>();
                var states = new Dictionary<string, List<string>>();;

                foreach (var scheduledJob in scheduledJobs)
                {
                    var job = scheduledJob;
                    jobs.Add(job.Element, 
                        redis.HashGet(String.Format(RedisStorage.Prefix + "job:{0}", job.Element), new RedisValue[] { "Type", "Method", "ParameterTypes", "Arguments" }).ToStringArray().ToList()
                        );
                    states.Add(job.Element,
                        redis.HashGet(String.Format(RedisStorage.Prefix + "job:{0}:state", job.Element),new RedisValue[] { "State", "ScheduledAt" }).ToStringArray().ToList()
                        );
                }
				

                return new JobList<ScheduledJobDto>(scheduledJobs
                    .Select(job => new KeyValuePair<string, ScheduledJobDto>(
                        job.Element,
                        new ScheduledJobDto
                        {
                            EnqueueAt = JobHelper.FromTimestamp((long) job.Score),
                            Job = TryToGetJob(jobs[job.Element][0], jobs[job.Element][1], jobs[job.Element][2], jobs[job.Element][3]),
                            ScheduledAt =
                                states[job.Element].Count > 1
                                    ? JobHelper.DeserializeNullableDateTime(states[job.Element][1])
                                    : null,
                            InScheduledState =
                                ScheduledState.StateName.Equals(states[job.Element][0], StringComparison.OrdinalIgnoreCase)
                        }))
                    .ToList());
            });
        }

        public IDictionary<DateTime, long> SucceededByDatesCount()
        {
            return UseConnection(redis => GetTimelineStats(redis, "succeeded"));
        }

        public IDictionary<DateTime, long> FailedByDatesCount()
        {
            return UseConnection(redis => GetTimelineStats(redis, "failed"));
        }

        public IList<ServerDto> Servers()
        {
            return UseConnection(redis =>
            {
                var serverNames = redis.SetMembers(RedisStorage.Prefix + "servers").Select(x => (string)x).ToList();

                if (serverNames.Count == 0)
                {
                    return new List<ServerDto>();
                }

                var servers = new Dictionary<string, List<string>>();
                var queues = new Dictionary<string, List<string>>();

                foreach (var serverName in serverNames)
                {
                    var name = serverName;
                    servers.Add(name,
                        redis.HashGet(String.Format(RedisStorage.Prefix + "server:{0}", name), new RedisValue[] { "WorkerCount", "StartedAt", "Heartbeat" })
                            .ToStringArray().ToList()
                        );
                    queues.Add(name,
                        redis.ListRange(String.Format(RedisStorage.Prefix + "server:{0}:queues", name))
                            .ToStringArray().ToList()
                        );
                    
                }


                return serverNames.Select(x => new ServerDto
                {
                    Name = x,
                    WorkersCount = int.Parse(servers[x][0]),
                    Queues = queues[x],
                    StartedAt = JobHelper.DeserializeDateTime(servers[x][1]),
                    Heartbeat = JobHelper.DeserializeNullableDateTime(servers[x][2])
                }).ToList();
            });
        }

        public JobList<FailedJobDto> FailedJobs(int from, int count)
        {
            return UseConnection(redis =>
            {
                var failedJobIds = redis.SortedSetRangeByRank(
                    RedisStorage.Prefix + "failed",
                    from,
                    from + count - 1)
					.ToStringArray();

                return GetJobsWithProperties(
                    redis,
                    failedJobIds,
                    null,
                    new[] { "FailedAt", "ExceptionType", "ExceptionMessage", "ExceptionDetails", "State", "Reason" },
                    (job, jobData, state) => new FailedJobDto
                    {
                        Job = job,
                        Reason = state[5],
                        FailedAt = JobHelper.DeserializeNullableDateTime(state[0]),
                        ExceptionType = state[1],
                        ExceptionMessage = state[2],
                        ExceptionDetails = state[3],
                        InFailedState = FailedState.StateName.Equals(state[4], StringComparison.OrdinalIgnoreCase)
                    });
            });
        }

        public JobList<SucceededJobDto> SucceededJobs(int from, int count)
        {
            return UseConnection(redis =>
            {
                var succeededJobIds = redis.ListRange(
                    RedisStorage.Prefix + "succeeded",
                    from,
                    from + count - 1)
					.ToStringArray();

                return GetJobsWithProperties(
                    redis,
                    succeededJobIds,
                    null,
                    new[] { "SucceededAt", "PerformanceDuration", "Latency", "State", "Result" },
                    (job, jobData, state) => new SucceededJobDto
                    {
                        Job = job,
                        Result = state[4],
                        SucceededAt = JobHelper.DeserializeNullableDateTime(state[0]),
                        TotalDuration = state[1] != null && state[2] != null
                            ? (long?) long.Parse(state[1]) + (long?) long.Parse(state[2])
                            : null,
                        InSucceededState = SucceededState.StateName.Equals(state[3], StringComparison.OrdinalIgnoreCase)
                    });
            });
        }

        public JobList<DeletedJobDto> DeletedJobs(int @from, int count)
        {
            return UseConnection(redis =>
            {
                var deletedJobIds = redis.ListRange(
                    RedisStorage.Prefix + "deleted",
                    from,
                    from + count - 1).ToStringArray();

                return GetJobsWithProperties(
                    redis,
                    deletedJobIds,
                    null,
                    new[] { "DeletedAt", "State" },
                    (job, jobData, state) => new DeletedJobDto
                    {
                        Job = job,
                        DeletedAt = JobHelper.DeserializeNullableDateTime(state[0]),
                        InDeletedState = DeletedState.StateName.Equals(state[1], StringComparison.OrdinalIgnoreCase)
                    });
            });
        }

        public IList<QueueWithTopEnqueuedJobsDto> Queues()
        {
            return UseConnection(redis =>
            {
                var queues = redis.SetMembers(RedisStorage.Prefix + "queues")
					.Select(x=> (string)x).ToList();
                var result = new List<QueueWithTopEnqueuedJobsDto>(queues.Count);

                foreach (var queue in queues)
                {
                    string[] firstJobIds = null;
                    long length = 0;
                    long fetched = 0;

                    firstJobIds = redis.ListRange(String.Format(RedisStorage.Prefix + "queue:{0}", queue), -5, -1).ToStringArray();
                    length = redis.ListLength(String.Format(RedisStorage.Prefix + "queue:{0}", queue));
                    fetched = redis.ListLength(String.Format(RedisStorage.Prefix + "queue:{0}:dequeued", queue));

                    var jobs = GetJobsWithProperties(
                        redis,
                        firstJobIds,
                        new[] { "State" },
                        new[] { "EnqueuedAt", "State" },
                        (job, jobData, state) => new EnqueuedJobDto
                        {
                            Job = job,
                            State = jobData[0],
                            EnqueuedAt = JobHelper.DeserializeNullableDateTime(state[0]),
                            InEnqueuedState = jobData[0].Equals(state[1], StringComparison.OrdinalIgnoreCase)
                        });

                    result.Add(new QueueWithTopEnqueuedJobsDto
                    {
                        Name = queue,
                        FirstJobs = jobs,
                        Length = length,
                        Fetched = fetched
                    });
                }

                return result;
            });
        }

        public JobList<EnqueuedJobDto> EnqueuedJobs(
            string queue, int from, int perPage)
        {
            return UseConnection(redis =>
            {
                var jobIds = redis.ListRange(
                    String.Format(RedisStorage.Prefix + "queue:{0}", queue),
                    from,
                    from + perPage - 1).ToStringArray();

                return GetJobsWithProperties(
                    redis,
                    jobIds,
                    new[] { "State" },
                    new[] { "EnqueuedAt", "State" },
                    (job, jobData, state) => new EnqueuedJobDto
                    {
                        Job = job,
                        State = jobData[0],
                        EnqueuedAt = JobHelper.DeserializeNullableDateTime(state[0]),
                        InEnqueuedState = jobData[0].Equals(state[1], StringComparison.OrdinalIgnoreCase)
                    });
            });
        }

        public JobList<FetchedJobDto> FetchedJobs(
            string queue, int from, int perPage)
        {
            return UseConnection(redis =>
            {
                var jobIds = redis.ListRange(
                    String.Format(RedisStorage.Prefix + "queue:{0}:dequeued", queue),
                    from, from + perPage - 1).ToStringArray();
				RedisValue[] rk = new RedisValue[1];
				
                return GetJobsWithProperties(
                    redis,
                    jobIds,
                    new[] { "State", "Fetched" },
                    null,
                    (job, jobData, state) => new FetchedJobDto
                    {
                        Job = job,
                        State = jobData[0],
                        FetchedAt = JobHelper.DeserializeNullableDateTime(jobData[1])
                    });
            });
        }

        public IDictionary<DateTime, long> HourlySucceededJobs()
        {
            return UseConnection(redis => GetHourlyTimelineStats(redis, "succeeded"));
        }

        public IDictionary<DateTime, long> HourlyFailedJobs()
        {
            return UseConnection(redis => GetHourlyTimelineStats(redis, "failed"));
        }

        public JobDetailsDto JobDetails(string jobId)
        {
            return UseConnection(redis =>
            {
                var job = redis.HashGetAll(String.Format(RedisStorage.Prefix + "job:{0}", jobId)).ToStringDictionary();
                if (job.Count == 0) return null;

                var hiddenProperties = new[] { "Type", "Method", "ParameterTypes", "Arguments", "State", "CreatedAt" };

                var historyList = redis.ListRange(String.Format(RedisStorage.Prefix + "job:{0}:history", jobId))
					.Select(x=> (string)x).ToList();

                var history = historyList
                    .Select(JobHelper.FromJson<Dictionary<string, string>>)
                    .ToList();

                var stateHistory = new List<StateHistoryDto>(history.Count);
                foreach (var entry in history)
                {
                    var dto = new StateHistoryDto
                    {
                        StateName = entry["State"],
                        Reason = entry.ContainsKey("Reason") ? entry["Reason"] : null,
                        CreatedAt = JobHelper.DeserializeDateTime(entry["CreatedAt"]),
                    };

                    // Each history item contains all of the information,
                    // but other code should not know this. We'll remove
                    // unwanted keys.
                    var stateData = new Dictionary<string, string>(entry);
                    stateData.Remove("State");
                    stateData.Remove("Reason");
                    stateData.Remove("CreatedAt");

                    dto.Data = stateData;
                    stateHistory.Add(dto);
                }

                // For compatibility
                if (!job.ContainsKey("Method")) job.Add("Method", null);
                if (!job.ContainsKey("ParameterTypes")) job.Add("ParameterTypes", null);

                return new JobDetailsDto
                {
                    Job = TryToGetJob(job["Type"], job["Method"], job["ParameterTypes"], job["Arguments"]),
                    CreatedAt =
                        job.ContainsKey("CreatedAt")
                            ? JobHelper.DeserializeDateTime(job["CreatedAt"])
                            : (DateTime?) null,
                    Properties =
                        job.Where(x => !hiddenProperties.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value),
                    History = stateHistory
                };
            });
        }

        private Dictionary<DateTime, long> GetHourlyTimelineStats(
            IDatabase redis, string type)
        {
            var endDate = DateTime.UtcNow;
            var dates = new List<DateTime>();
            for (var i = 0; i < 24; i++)
            {
                dates.Add(endDate);
                endDate = endDate.AddHours(-1);
            }

            var keys = dates.Select(x => String.Format(RedisStorage.Prefix + "stats:{0}:{1}", type, x.ToString("yyyy-MM-dd-HH"))).ToArray();
            var valuesMap = redis.GetValuesMap(keys);

            var result = new Dictionary<DateTime, long>();
            for (var i = 0; i < dates.Count; i++)
            {
                long value;
                if (!long.TryParse(valuesMap[valuesMap.Keys.ElementAt(i)], out value))
                {
                    value = 0;
                }

                result.Add(dates[i], value);
            }

            return result;
        }

        private Dictionary<DateTime, long> GetTimelineStats(
            IDatabase redis, string type)
        {
            var endDate = DateTime.UtcNow.Date;
            var startDate = endDate.AddDays(-7);
            var dates = new List<DateTime>();

            while (startDate <= endDate)
            {
                dates.Add(endDate);
                endDate = endDate.AddDays(-1);
            }

            var stringDates = dates.Select(x => x.ToString("yyyy-MM-dd")).ToList();
            var keys = stringDates.Select(x => String.Format(RedisStorage.Prefix + "stats:{0}:{1}", type, x)).ToArray();

            var valuesMap = redis.GetValuesMap(keys);

            var result = new Dictionary<DateTime, long>();
            for (var i = 0; i < stringDates.Count; i++)
            {
                long value;
                if (!long.TryParse(valuesMap[valuesMap.Keys.ElementAt(i)], out value))
                {
                    value = 0;
                }
                result.Add(dates[i], value);
            }

            return result;
        }

        private JobList<T> GetJobsWithProperties<T>(
            IDatabase redis,
            string[] jobIds,
            string[] properties,
            string[] stateProperties,
            Func<Job, List<string>, List<string>, T> selector)
        {
            if (jobIds.Length == 0) return new JobList<T>(new List<KeyValuePair<string, T>>());

            var jobs = new Dictionary<string, RedisValue[]>(jobIds.Length);
            var states = new Dictionary<string, RedisValue[]>(jobIds.Length);

            properties = properties ?? new string[0];

            foreach (var jobId in jobIds)
            {
                var id = jobId;
                jobs.Add(jobId, 
                    redis.HashGet(String.Format(RedisStorage.Prefix + "job:{0}", id), 
                        properties.Union(new[] { "Type", "Method", "ParameterTypes", "Arguments" }).Select(x=> (RedisValue)x).ToArray()));
                
				if (stateProperties != null)
                    states.Add(jobId, redis.HashGet(String.Format(RedisStorage.Prefix + "job:{0}:state", id), stateProperties.Select(x => (RedisValue)x).ToArray()));
            }

			
			var jobList = new JobList<T>(jobIds
                .Select(x => new
                {
                    JobId = x,
                    Job = jobs[x].ToStringArray().ToList(),
                    Method = TryToGetJob(
                        jobs[x][properties.Length],
                        jobs[x][properties.Length + 1],
                        jobs[x][properties.Length + 2],
                        jobs[x][properties.Length + 3]),
                    State = states.ContainsKey(x) ? states[x].ToStringArray().ToList() : null
                })
                .Select(x => new KeyValuePair<string, T>(
                    x.JobId,
                    x.Job.TrueForAll(y => y == null)
                        ? default(T)
                        : selector(x.Method, x.Job, x.State)))
                .ToList());
			return jobList;
        }

        public long SucceededListCount()
        {
            return UseConnection(redis => redis.ListLength(RedisStorage.Prefix + "succeeded"));
        }

        public StatisticsDto GetStatistics()
        {
            return UseConnection(redis =>
            {
                var stats = new StatisticsDto();

                var queues = redis.SetMembers(RedisStorage.Prefix + "queues");

                stats.Servers = redis.SetLength(RedisStorage.Prefix + "servers");
                stats.Queues = redis.SetLength(RedisStorage.Prefix + "queues");
                stats.Scheduled = redis.SortedSetLength(RedisStorage.Prefix + "schedule");
                stats.Processing = redis.SortedSetLength(RedisStorage.Prefix + "processing");
                var succeeded = redis.StringGet(RedisStorage.Prefix + "stats:succeeded");
                stats.Succeeded = long.Parse(succeeded.HasValue ? (string)succeeded : "0");
                stats.Failed = redis.SortedSetLength(RedisStorage.Prefix + "failed");
                var deleted = redis.StringGet(RedisStorage.Prefix + "stats:deleted");
				stats.Deleted = long.Parse(deleted.HasValue ?  (string)deleted: "0");
                stats.Recurring = redis.SortedSetLength(RedisStorage.Prefix + "recurring-jobs");
                foreach (var queue in queues)
                {
                    var queueName = queue;
                    stats.Enqueued += redis.ListLength(String.Format(RedisStorage.Prefix + "queue:{0}", queueName));
                }
				
                return stats;
            });
        }

        private T UseConnection<T>(Func<IDatabase, T> action)
        {
			return action(_database);
        }

        private static Job TryToGetJob(
            string type, string method, string parameterTypes, string arguments)
        {
            try
            {
                return new InvocationData(
                    type,
                    method,
                    parameterTypes,
                    arguments).Deserialize();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
