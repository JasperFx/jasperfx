﻿using Shouldly;

namespace CommandLineTests.Resources
{
    public class resource_filtering : ResourceCommandContext
    {
        [Fact]
        public async Task uses_resource_source()
        {
            var blue = AddResource("blue", "color");
            var red = AddResource("red", "color");

            AddSource(col =>
            {
                col.Add("purple", "color");
                col.Add("orange", "color");
            });
            
            AddSource(col =>
            {
                col.Add("green", "color");
                col.Add("white", "color");
            });
            
            var resources = await applyTheResourceFiltering();

            var colors = resources.Select(x => x.Name).OrderBy(x => x)
                .ToList();
            
            colors.ShouldBe(["blue", "green", "orange", "purple", "red", "white"]);
        }
        
        [Fact]
        public async Task no_filtering()
        {
            var blue = AddResource("blue", "color");
            var red = AddResource("red", "color");
    
            var tx = AddResource("tx", "state");
            var ar = AddResource("ar", "state");
    
            var resources = await applyTheResourceFiltering();
    
            resources.Count.ShouldBe(4);
            
            resources.ShouldContain(blue);
            resources.ShouldContain(red);
            resources.ShouldContain(tx);
            resources.ShouldContain(ar);
        }
    
        [Fact]
        public async Task filter_by_name()
        {
            var blue = AddResource("blue", "color");
            var red = AddResource("red", "color");
    
            var tx = AddResource("tx", "state");
            var ar = AddResource("ar", "state");
    
            theInput.NameFlag = "tx";
    
            var resources = await applyTheResourceFiltering();
            resources.Single()
                .ShouldBe(tx);
        }
        
        [Fact]
        public async Task filter_by_type()
        {
            var blue = AddResource("blue", "color");
            var red = AddResource("red", "color");
            var green = AddResource("green", "color");
    
            var tx = AddResource("tx", "state");
            var ar = AddResource("ar", "state");
            var mo = AddResource("mo", "state");
    
            theInput.TypeFlag = "color";
            var resources = await applyTheResourceFiltering();
            
            resources.Count.ShouldBe(3);
            resources.ShouldContain(blue);
            resources.ShouldContain(red);
            resources.ShouldContain(green);
        }
    }
}

