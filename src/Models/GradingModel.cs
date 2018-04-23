using System.Collections.Generic;

namespace gcbulkgrader.Models
{
    public class GradingModel : PersonModel
    {
        public string CourseId { get; set; }
        public string CourseName { get; set; }
        public IList<AssignmentModel> Assignments { get; set; }
    }
}
