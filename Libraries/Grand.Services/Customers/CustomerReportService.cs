using System;
using System.Linq;
using Grand.Core;
using Grand.Core.Data;
using Grand.Core.Domain.Customers;
using Grand.Core.Domain.Orders;
using Grand.Core.Domain.Payments;
using Grand.Core.Domain.Shipping;
using Grand.Services.Helpers;
using MongoDB.Driver.Linq;
using System.Collections.Generic;
using MongoDB.Driver;

namespace Grand.Services.Customers
{
    /// <summary>
    /// Customer report service
    /// </summary>
    public partial class CustomerReportService : ICustomerReportService
    {
        #region Fields

        private readonly IRepository<Customer> _customerRepository;
        private readonly IRepository<Order> _orderRepository;
        private readonly ICustomerService _customerService;
        private readonly IDateTimeHelper _dateTimeHelper;

        #endregion

        #region Ctor

        /// <summary>
        /// Ctor
        /// </summary>
        /// <param name="customerRepository">Customer repository</param>
        /// <param name="orderRepository">Order repository</param>
        /// <param name="customerService">Customer service</param>
        /// <param name="dateTimeHelper">Date time helper</param>
        public CustomerReportService(IRepository<Customer> customerRepository,
            IRepository<Order> orderRepository, ICustomerService customerService,
            IDateTimeHelper dateTimeHelper)
        {
            this._customerRepository = customerRepository;
            this._orderRepository = orderRepository;
            this._customerService = customerService;
            this._dateTimeHelper = dateTimeHelper;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Get best customers
        /// </summary>
        /// <param name="createdFromUtc">Order created date from (UTC); null to load all records</param>
        /// <param name="createdToUtc">Order created date to (UTC); null to load all records</param>
        /// <param name="os">Order status; null to load all records</param>
        /// <param name="ps">Order payment status; null to load all records</param>
        /// <param name="ss">Order shipment status; null to load all records</param>
        /// <param name="orderBy">1 - order by order total, 2 - order by number of orders</param>
        /// <param name="pageIndex">Page index</param>
        /// <param name="pageSize">Page size</param>
        /// <returns>Report</returns>
        public virtual IPagedList<BestCustomerReportLine> GetBestCustomersReport(DateTime? createdFromUtc,
            DateTime? createdToUtc, OrderStatus? os, PaymentStatus? ps, ShippingStatus? ss, int orderBy,
            int pageIndex = 0, int pageSize = 214748364)
        {
            int? orderStatusId = null;
            if (os.HasValue)
                orderStatusId = (int)os.Value;

            int? paymentStatusId = null;
            if (ps.HasValue)
                paymentStatusId = (int)ps.Value;

            int? shippingStatusId = null;
            if (ss.HasValue)
                shippingStatusId = (int)ss.Value;

            var query = _orderRepository.Table;
            query = query.Where(o => !o.Deleted);
            if (orderStatusId.HasValue)
                query = query.Where(o => o.OrderStatusId == orderStatusId.Value);
            if (paymentStatusId.HasValue)
                query = query.Where(o => o.PaymentStatusId == paymentStatusId.Value);
            if (shippingStatusId.HasValue)
                query = query.Where(o => o.ShippingStatusId == shippingStatusId.Value);
            if (createdFromUtc.HasValue)
                query = query.Where(o => createdFromUtc.Value <= o.CreatedOnUtc);
            if (createdToUtc.HasValue)
                query = query.Where(o => createdToUtc.Value >= o.CreatedOnUtc);

            var query2 = from co in query
                         group co by co.CustomerId into g
                         select new
                         {
                             CustomerId = g.Key,
                             OrderTotal = g.Sum(x => x.OrderTotal),
                             OrderCount = g.Count()
                         };
            switch (orderBy)
            {
                case 1:
                    {
                        query2 = query2.OrderByDescending(x => x.OrderTotal);
                    }
                    break;
                case 2:
                    {
                        query2 = query2.OrderByDescending(x => x.OrderCount);
                    }
                    break;
                default:
                    throw new ArgumentException("Wrong orderBy parameter", "orderBy");
            }

            var tmp = new PagedList<dynamic>(query2, pageIndex, pageSize);
            return new PagedList<BestCustomerReportLine>(tmp.Select(x => new BestCustomerReportLine
            {
                CustomerId = x.CustomerId,
                OrderTotal = x.OrderTotal,
                OrderCount = x.OrderCount
            }),
                tmp.PageIndex, tmp.PageSize, tmp.TotalCount);
        }

        /// <summary>
        /// Gets a report of customers registered in the last days
        /// </summary>
        /// <param name="days">Customers registered in the last days</param>
        /// <returns>Number of registered customers</returns>
        public virtual int GetRegisteredCustomersReport(int days)
        {
            DateTime date = _dateTimeHelper.ConvertToUserTime(DateTime.Now).AddDays(-days);

            var registeredCustomerRole = _customerService.GetCustomerRoleBySystemName(SystemCustomerRoleNames.Registered);
            if (registeredCustomerRole == null)
                return 0;

            var query = from c in _customerRepository.Table
                        where !c.Deleted &&
                        c.CustomerRoles.Any(cr => cr.Id == registeredCustomerRole.Id) &&
                        c.CreatedOnUtc >= date
                        //&& c.CreatedOnUtc <= DateTime.UtcNow
                        select c;
            int count = query.Count();
            return count;
        }


        /// <summary>
        /// Get "customer by time" report
        /// </summary>
        /// <param name="startTimeUtc">Start date</param>
        /// <param name="endTimeUtc">End date</param>
        /// <returns>Result</returns>
        public virtual IList<CustomerByTimeReportLine> GetCustomerByTimeReport(DateTime? startTimeUtc = null,
            DateTime? endTimeUtc = null)

        {
            List<CustomerByTimeReportLine> report = new List<CustomerByTimeReportLine>();
            if (!startTimeUtc.HasValue)
                startTimeUtc = DateTime.MinValue;
            if (!endTimeUtc.HasValue)
                endTimeUtc = DateTime.UtcNow;

            var endTime = new DateTime(endTimeUtc.Value.Year, endTimeUtc.Value.Month, endTimeUtc.Value.Day, 23, 59, 00);

            var builder = Builders<Customer>.Filter;
            var customerRoleRegister = _customerService.GetCustomerRoleBySystemName(SystemCustomerRoleNames.Registered).Id;
            var filter = builder.Where(o => !o.Deleted);
            filter = filter & builder.Where(o => o.CreatedOnUtc >= startTimeUtc && o.CreatedOnUtc <= endTime);
            filter = filter & builder.Where(o => o.CustomerRoles.Any(y => y.Id == customerRoleRegister));

            var daydiff = (endTimeUtc.Value - startTimeUtc.Value).TotalDays;
            if (daydiff > 32)
            {
                var query = _customerRepository.Collection.Aggregate().Match(filter).Group(x =>
                    new { Year = x.CreatedOnUtc.Year, Month = x.CreatedOnUtc.Month },
                    g => new { Okres = g.Key, Count = g.Count() }).SortBy(x => x.Okres).ToList();
                foreach (var item in query)
                {
                    report.Add(new CustomerByTimeReportLine()
                    {
                        Time = item.Okres.Year.ToString() + "-" + item.Okres.Month.ToString(),
                        Registered = item.Count,
                    });
                }
            }
            else
            {
                var query = _customerRepository.Collection.Aggregate().Match(filter).Group(x =>
                    new { Year = x.CreatedOnUtc.Year, Month = x.CreatedOnUtc.Month, Day = x.CreatedOnUtc.Day },
                    g => new { Okres = g.Key, Count = g.Count() }).SortBy(x => x.Okres).ToList();
                foreach (var item in query)
                {
                    report.Add(new CustomerByTimeReportLine()
                    {
                        Time = item.Okres.Year.ToString() + "-" + item.Okres.Month.ToString() + "-" + item.Okres.Day.ToString(),
                        Registered = item.Count,
                    });
                }
            }



            return report;
        }

        #endregion
    }
}