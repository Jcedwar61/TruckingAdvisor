namespace TruckingAdvisor.Core.Mapping;

/// <summary>
/// US state for city names used by American Truck Simulator (they're real US cities).
/// Names that exist in several states (Springfield, Portland, Columbus...) are left out on purpose:
/// no state is better than a wrong one.
/// </summary>
public static class CityStates
{
    private static readonly Dictionary<string, string> Map = Build();

    public static string? StateFor(string city) =>
        string.IsNullOrWhiteSpace(city) ? null : Map.TryGetValue(city.Trim(), out var s) ? s : null;

    private static Dictionary<string, string> Build()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Same name in more than one state (in ATS or in real life): never guess.
            "Springfield", "Portland", "Columbus", "Kansas City", "Texarkana", "Salina", "Sidney", "Longview", "Ontario",
            "Carlsbad", "Aberdeen", "Green River", "Jackson", "Lafayette", "Monroe", "Alexandria", "Columbia", "Rochester",
            "Aurora", "Newport", "Lancaster", "Victoria", "Bryan", "Huron", "Moscow", "Clinton", "Norfolk", "Douglas", "Burlington"
        };
        void Add(string state, params string[] cities)
        {
            foreach (var c in cities)
            {
                if (ambiguous.Contains(c)) continue;
                if (d.TryGetValue(c, out var existing) && existing != state) { ambiguous.Add(c); d.Remove(c); continue; }
                d[c] = state;
            }
        }

        Add("CA", "Bakersfield", "Barstow", "Carlsbad", "Crescent City", "El Centro", "Eureka", "Fresno", "Hilt", "Hornbrook",
            "Huron", "Los Angeles", "Modesto", "Oakdale", "Oakland", "Oxnard", "Redding", "Sacramento", "San Diego",
            "San Francisco", "San Jose", "San Rafael", "Santa Cruz", "Santa Maria", "Stockton", "Truckee", "Ukiah", "Bishop",
            "Blythe", "Chico", "Bakersfield", "Lancaster", "Ontario", "Palm Springs", "Santa Barbara", "Salinas", "Merced",
            "Visalia", "Victorville", "Indio", "San Luis Obispo", "Alturas", "Susanville", "Yreka");
        Add("NV", "Carson City", "Elko", "Ely", "Jackpot", "Las Vegas", "Pioche", "Primm", "Reno", "Tonopah", "Winnemucca",
            "Fallon", "Hawthorne", "Mesquite", "Laughlin");
        Add("AZ", "Ehrenberg", "Flagstaff", "Holbrook", "Kingman", "Nogales", "Page", "Phoenix", "San Simon", "Show Low",
            "Sierra Vista", "Tucson", "Yuma", "Prescott", "Winslow", "Lake Havasu City");
        Add("NM", "Alamogordo", "Albuquerque", "Artesia", "Carlsbad", "Clovis", "Farmington", "Gallup", "Hobbs", "Las Cruces",
            "Raton", "Roswell", "Santa Fe", "Socorro", "Tucumcari", "Deming", "Lordsburg");
        Add("OR", "Astoria", "Bend", "Burns", "Coos Bay", "Eugene", "Klamath Falls", "Lakeview", "Medford", "Ontario",
            "Pendleton", "Salem", "The Dalles", "Newport", "Roseburg", "Hermiston", "La Grande");
        Add("WA", "Aberdeen", "Bellingham", "Colville", "Everett", "Grand Coulee", "Kennewick", "Longview", "Olympia",
            "Omak", "Port Angeles", "Seattle", "Spokane", "Tacoma", "Wenatchee", "Yakima", "Moses Lake", "Pasco", "Richland", "Walla Walla");
        Add("UT", "Cedar City", "Logan", "Moab", "Ogden", "Price", "Provo", "Salina", "Salt Lake City", "St. George", "Vernal",
            "Green River", "Tooele", "Blanding");
        Add("ID", "Boise", "Coeur d'Alene", "Grangeville", "Idaho Falls", "Ketchum", "Lewiston", "Nampa", "Pocatello",
            "Salmon", "Sandpoint", "Twin Falls", "Burley", "Mountain Home");
        Add("CO", "Alamosa", "Burlington", "Colorado Springs", "Denver", "Durango", "Fort Collins", "Grand Junction",
            "Lamar", "Montrose", "Pueblo", "Rifle", "Sterling", "Steamboat Springs", "Trinidad", "Craig", "Glenwood Springs",
            "Greeley", "Gunnison", "Limon", "Vail");
        Add("WY", "Casper", "Cheyenne", "Evanston", "Gillette", "Jackson", "Laramie", "Rock Springs", "Rawlins", "Riverton",
            "Sheridan", "Cody", "Worland", "Douglas", "Lusk", "Torrington");
        Add("MT", "Billings", "Bozeman", "Butte", "Glasgow", "Great Falls", "Havre", "Helena", "Kalispell", "Miles City",
            "Missoula", "Sidney", "Wolf Point", "Lewistown", "Shelby");
        Add("TX", "Abilene", "Amarillo", "Austin", "Beaumont", "Brownsville", "Bryan", "Corpus Christi", "Dalhart", "Dallas",
            "Del Rio", "El Paso", "Fort Stockton", "Fort Worth", "Galveston", "Houston", "Huntsville", "Killeen", "Laredo",
            "Lubbock", "Lufkin", "McAllen", "Midland", "Odessa", "Pecos", "San Angelo", "San Antonio",
            "Sherman", "Texarkana", "Tyler", "Van Horn", "Victoria", "Waco", "Wichita Falls", "Childress", "Junction", "Kingsville");
        Add("OK", "Ardmore", "Clinton", "Enid", "Guymon", "Idabel", "Lawton", "McAlester", "Muskogee", "Oklahoma City",
            "Stillwater", "Tulsa", "Woodward", "Elk City", "Ponca City");
        Add("KS", "Colby", "Dodge City", "Emporia", "Garden City", "Hays", "Hutchinson", "Liberal", "Pittsburg",
            "Topeka", "Wichita", "Great Bend");
        Add("NE", "Alliance", "Grand Island", "Kearney", "Lincoln", "McCook", "Norfolk", "North Platte", "Omaha",
            "O'Neill", "Scottsbluff", "Valentine");
        Add("AR", "El Dorado", "Fayetteville", "Fort Smith", "Harrison", "Hot Springs", "Jonesboro", "Little Rock",
            "Pine Bluff", "Russellville");
        Add("MO", "Cape Girardeau", "Jefferson City", "Joplin", "Kansas City", "Kirksville", "Poplar Bluff",
            "Rolla", "Sikeston", "St. Joseph", "St. Louis");
        Add("LA", "Alexandria", "Baton Rouge", "Lafayette", "Lake Charles", "Monroe", "New Orleans", "Shreveport", "Houma");
        Add("IA", "Cedar Rapids", "Council Bluffs", "Davenport", "Des Moines", "Dubuque", "Mason City", "Ottumwa", "Sioux City", "Waterloo");
        Add("SD", "Pierre", "Rapid City", "Sioux Falls", "Watertown", "Mitchell");
        Add("ND", "Bismarck", "Fargo", "Grand Forks", "Minot", "Williston", "Dickinson");
        Add("MN", "Duluth", "Minneapolis", "St. Cloud", "Saint Paul");
        Add("IL", "Chicago", "Peoria", "Rockford", "Champaign", "Joliet");
        return d;
    }
}
