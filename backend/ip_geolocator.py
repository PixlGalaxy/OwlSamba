import requests

def get_ip_location(ip):

    try:
        
        url = f"http://ip-api.com/json/{ip}"
        response = requests.get(url)
        response.raise_for_status() 
        
        data = response.json()
        
        if data['status'] == 'success':
            # Return the location information
            return {
                "IP": data['query'],
                "Country": data['country'],
                "Region": data['regionName'],
                "City": data['city'],
                "Latitude": data['lat'],
                "Longitude": data['lon'],
                "ISP": data['isp']
            }
        
        else:
            return {"Error": f"Could not retrieve information for IP: {ip}"}
    
    except requests.RequestException as e:
        return {"Error": f"Error connecting to the service: {e}"}

# Example usage
ip = "8.8.8.8"  # Replace this with the IP address you want to locate
location = get_ip_location(ip)
print(location)
