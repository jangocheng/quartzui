﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Host.Entity;
using Microsoft.Data.Sqlite;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.AdoJobStore;
using Quartz.Impl.AdoJobStore.Common;
using Quartz.Impl.Matchers;
using Quartz.Simpl;
using Quartz.Util;
using Serilog;
using System.Linq;
using Quartz.Impl.Triggers;

namespace Host
{
    /// <summary>
    /// 调度中心
    /// </summary>
    public class SchedulerCenter
    {
        /// <summary>
        /// 任务调度对象
        /// </summary>
        public static readonly SchedulerCenter Instance;
        static SchedulerCenter()
        {
            Instance = new SchedulerCenter();
        }

        private IScheduler _scheduler;
        /// <summary>
        /// 返回任务计划（调度器）
        /// </summary>
        /// <returns></returns>
        private IScheduler Scheduler
        {
            get
            {
                if (_scheduler != null)
                {
                    return _scheduler;
                }

                //如果不存在sqlite数据库，则创建
                if (!File.Exists("File/sqliteScheduler.db"))
                {
                    using (var connection = new SqliteConnection("Data Source=File/sqliteScheduler.db"))
                    {
                        connection.OpenAsync().Wait();
                        string sql = File.ReadAllTextAsync("tables_sqlite.sql").Result;
                        var command = new SqliteCommand(sql, connection);
                        command.ExecuteNonQuery();
                        connection.Close();
                    }
                }

                //MySql存储
                //DBConnectionManager.Instance.AddConnectionProvider("default", new DbProvider("MySql", "server=192.168.10.133;user id=root;password=pass;persistsecurityinfo=True;database=quartz"));
                DBConnectionManager.Instance.AddConnectionProvider("default", new DbProvider("SQLite-Microsoft", "Data Source=File/sqliteScheduler.db"));
                var serializer = new JsonObjectSerializer();
                serializer.Initialize();
                var jobStore = new JobStoreTX
                {
                    DataSource = "default",
                    TablePrefix = "QRTZ_",
                    InstanceId = "AUTO",
                    //DriverDelegateType = typeof(MySQLDelegate).AssemblyQualifiedName, //MySql存储
                    DriverDelegateType = typeof(SQLiteDelegate).AssemblyQualifiedName,  //SQLite存储
                    ObjectSerializer = serializer
                };
                DirectSchedulerFactory.Instance.CreateScheduler("benny" + "Scheduler", "AUTO", new DefaultThreadPool(), jobStore);
                _scheduler = SchedulerRepository.Instance.Lookup("benny" + "Scheduler").Result;

                _scheduler.Start();//默认开始调度器
                return _scheduler;
            }
        }

        /// <summary>
        /// 添加一个工作调度
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        public async Task<BaseResult> AddScheduleJob(ScheduleEntity entity)
        {
            var result = new BaseResult();
            try
            {
                //检查任务是否已存在
                var jobKey = new JobKey(entity.JobName, entity.JobGroup);
                if (await Scheduler.CheckExists(jobKey))
                {
                    result.Code = 500;
                    result.Msg = "任务已存在";
                    return result;
                }
                //http请求配置
                var httpDir = new Dictionary<string, string>()
                {
                    { "RequestUrl",entity.RequestUrl},
                    { "RequestParameters",entity.RequestParameters},
                    { "RequestType", ((int)entity.RequestType).ToString()},
                };
                // 定义这个工作，并将其绑定到我们的IJob实现类                
                IJobDetail job = JobBuilder.CreateForAsync<HttpJob>()
                    .SetJobData(new JobDataMap(httpDir))
                    .WithDescription(entity.Description)
                    .WithIdentity(entity.JobName, entity.JobGroup)
                    .Build();
                // 创建触发器
                ITrigger trigger;
                //校验是否正确的执行周期表达式
                if (!string.IsNullOrEmpty(entity.Cron) && CronExpression.IsValidExpression(entity.Cron))
                {
                    trigger = CreateCronTrigger(entity);
                }
                else
                {
                    trigger = CreateSimpleTrigger(entity);
                }

                // 告诉Quartz使用我们的触发器来安排作业
                await Scheduler.ScheduleJob(job, trigger);
                result.Code = 200;
            }
            catch (Exception ex)
            {
                result.Code = 505;
                result.Msg = ex.Message;
            }
            return result;
        }

        /// <summary>
        /// 暂停/删除 指定的计划
        /// </summary>
        /// <param name="jobGroup">任务分组</param>
        /// <param name="jobName">任务名称</param>
        /// <param name="isDelete">停止并删除任务</param>
        /// <returns></returns>
        public async Task<BaseResult> StopOrDelScheduleJob(string jobGroup, string jobName, bool isDelete = false)
        {
            BaseResult result;
            try
            {
                await Scheduler.PauseJob(new JobKey(jobName, jobGroup));
                if (isDelete)
                {
                    await Scheduler.DeleteJob(new JobKey(jobName, jobGroup));
                }
                result = new BaseResult
                {
                    Code = 200,
                    Msg = "停止任务计划成功！"
                };
            }
            catch (Exception ex)
            {
                result = new BaseResult
                {
                    Code = 505,
                    Msg = "停止任务计划失败"
                };
            }
            return result;
        }

        /// <summary>
        /// 恢复运行暂停的任务
        /// </summary>
        /// <param name="jobName">任务名称</param>
        /// <param name="jobGroup">任务分组</param>
        public async Task<BaseResult> ResumeJob(string jobGroup, string jobName)
        {
            BaseResult result = new BaseResult();
            try
            {
                //检查任务是否存在
                var jobKey = new JobKey(jobName, jobGroup);
                if (await Scheduler.CheckExists(jobKey))
                {
                    //任务已经存在则暂停任务
                    await Scheduler.ResumeJob(jobKey);
                    Log.Information(string.Format("任务“{0}”恢复运行", jobName));
                }
            }
            catch (Exception ex)
            {
                Log.Error(string.Format("恢复任务失败！{0}", ex));
            }
            return result;
        }

        /// <summary>
        /// 开启调度器
        /// </summary>
        /// <returns></returns>
        public async Task<bool> StartSchedule()
        {
            //开启调度器
            if (Scheduler.InStandbyMode)
            {
                await Scheduler.Start();
                Log.Information("任务调度启动！");
            }
            return Scheduler.InStandbyMode;
        }

        /// <summary>
        /// 停止任务调度
        /// </summary>
        public async Task<bool> StopSchedule()
        {
            //判断调度是否已经关闭
            if (!Scheduler.InStandbyMode)
            {
                //等待任务运行完成
                await Scheduler.Standby(); //TODO  注意：Shutdown后Start会报错，所以这里使用暂停。
                Log.Information("任务调度暂停！");
            }
            return !Scheduler.InStandbyMode;
        }

        /// <summary>
        /// 创建类型Simple的触发器
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        private ITrigger CreateSimpleTrigger(ScheduleEntity entity)
        {
            //作业触发器
            if (entity.RunTimes > 0)
            {
                return TriggerBuilder.Create()
               .WithIdentity(entity.JobName, entity.JobGroup)
               .StartAt(entity.BeginTime)//开始时间
               .EndAt(entity.EndTime)//结束数据
               .WithSimpleSchedule(x => x
                   .WithIntervalInSeconds(entity.IntervalSecond.Second)//执行时间间隔，单位秒
                   .WithRepeatCount(entity.RunTimes))//执行次数、默认从0开始
                   .ForJob(entity.JobName, entity.JobGroup)//作业名称
               .Build();
            }
            else
            {
                return TriggerBuilder.Create()
               .WithIdentity(entity.JobName, entity.JobGroup)
               .StartAt(entity.BeginTime)//开始时间
               .EndAt(entity.EndTime)//结束数据
               .WithSimpleSchedule(x => x
                   .WithIntervalInSeconds(entity.IntervalSecond.Second)//执行时间间隔，单位秒
                   .RepeatForever())//无限循环
                   .ForJob(entity.JobName, entity.JobGroup)//作业名称
               .Build();
            }

        }

        /// <summary>
        /// 创建类型Cron的触发器
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        private ITrigger CreateCronTrigger(ScheduleEntity entity)
        {
            // 作业触发器
            return TriggerBuilder.Create()
                   .WithIdentity(entity.JobName, entity.JobGroup)
                   .StartAt(entity.BeginTime)//开始时间
                   .EndAt(entity.EndTime)//结束时间
                   .WithCronSchedule(entity.Cron)//指定cron表达式
                   .ForJob(entity.JobName, entity.JobGroup)//作业名称
                   .Build();
        }

        /// <summary>
        /// 获取所有Job
        /// </summary>
        /// <returns></returns>
        public async Task<List<JobInfoEntity>> GetAllJob()
        {
            List<JobKey> jboKeyList = new List<JobKey>();
            List<JobInfoEntity> jobInfoList = new List<JobInfoEntity>();
            var groupNames = await Scheduler.GetJobGroupNames();
            foreach (var groupName in groupNames)
            {
                jboKeyList.AddRange(await Scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals(groupName)));
                jobInfoList.Add(new JobInfoEntity() { GroupName = groupName });
            }
            foreach (var jobKey in jboKeyList)
            {
                var jobDetail = await Scheduler.GetJobDetail(jobKey);
                var triggersList = await Scheduler.GetTriggersOfJob(jobKey);
                var triggers = triggersList.AsEnumerable().FirstOrDefault();

                var interval = string.Empty;
                if (triggers is SimpleTriggerImpl)
                    interval = (triggers as SimpleTriggerImpl)?.RepeatInterval.ToString();
                else
                    interval = (triggers as CronTriggerImpl)?.CronExpressionString;

                foreach (var jobInfo in jobInfoList)
                {
                    if (jobInfo.GroupName == jobKey.Group)
                    {
                        jobInfo.JobInfoList.Add(new JobInfo()
                        {
                            Name = jobKey.Name,
                            LastErrMsg = jobDetail.JobDataMap.GetString("Exception"),
                            RequestUrl = jobDetail.JobDataMap.GetString("RequestUrl"),
                            TriggerState = await Scheduler.GetTriggerState(triggers.Key),
                            PreviousFireTime = triggers.GetPreviousFireTimeUtc()?.LocalDateTime,
                            NextFireTime = triggers.GetNextFireTimeUtc()?.LocalDateTime,
                            BeginTime = triggers.StartTimeUtc.LocalDateTime,
                            Interval = interval,
                            EndTime = triggers.EndTimeUtc?.LocalDateTime
                        });
                        continue;
                    }
                }
            }
            return jobInfoList;
        }
    }
}

