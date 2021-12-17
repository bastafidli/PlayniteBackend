﻿using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Newtonsoft.Json;
using PlayniteServices.Filters;
using PlayniteServices.Models.IGDB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace PlayniteServices.Controllers.IGDB.DataGetter
{
    public class AlternativeNames : DataGetter<AlternativeName>
    {
        public AlternativeNames(IgdbApi igdbApi) : base(igdbApi, "alternative_names")
        {
            Collection.Indexes.CreateOne(new CreateIndexModel<AlternativeName>(Builders<AlternativeName>.IndexKeys.Text(x => x.name)));
        }
    }
}
