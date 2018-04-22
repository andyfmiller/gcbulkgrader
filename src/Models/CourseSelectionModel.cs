using Microsoft.AspNetCore.Mvc.Rendering;

namespace gcbulkgrader.Models
{
    public class CourseSelectionModel
    {
        public SelectList Courses { get; set; }
        public string PersonName { get; set; }
        public string UserId { get; set; }
    }
}
