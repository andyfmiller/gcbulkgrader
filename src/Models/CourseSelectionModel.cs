using System.Collections.Generic;

namespace gcbulkgrader.Models
{
    public class CourseSelectionModel : PersonModel
    {
        public IList<CourseModel> Courses { get; set; }
        public string CourseId { get; set; }
    }
}
